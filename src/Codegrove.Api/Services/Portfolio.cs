using System.Buffers.Binary;
using System.Net;
using System.Text.RegularExpressions;
using Codegrove.Api.Data.Entities;

namespace Codegrove.Api.Services;

/// <summary>How a portfolio project gets its name, and the rules for a name the learner types.</summary>
public static partial class PortfolioNames
{
    /// <summary>
    /// The name the learner gave their app: the text of its first plain <c>&lt;h1&gt;</c> (the capstone's "Make it
    /// yours" asks them to rename it), looked for in <c>web/App.jsx</c> first, then the other web files. Then the
    /// page's &lt;title&gt; in <c>web/index.html</c>, the name Download project uses. Then <paramref name="fallback"/>
    /// (the lesson title). An <c>&lt;h1&gt;</c> built from code (<c>{name}</c>) is skipped, and so is any name that
    /// doesn't pass the content filter: a filtered word or personal information never becomes a project name.
    /// </summary>
    public static string FromPage(IReadOnlyDictionary<string, string> files, string fallback, IContentFilter filter)
    {
        var webFiles = files
            .Where(f => f.Key.StartsWith("web/", StringComparison.Ordinal) && (f.Key.EndsWith(".jsx") || f.Key.EndsWith(".js") || f.Key.EndsWith(".html")))
            .OrderBy(f => f.Key == "web/App.jsx" ? 0 : 1).ThenBy(f => f.Key, StringComparer.Ordinal);
        var candidates = webFiles.Select(f => Heading().Match(f.Value))
            .Concat([TitleTag().Match(files.TryGetValue("web/index.html", out var page) ? page : "")])
            .Where(m => m.Success)
            .Select(m => Clean(WebUtility.HtmlDecode(m.Groups[1].Value)));

        return candidates.FirstOrDefault(name => name.Length > 0 && filter.CheckText(name).Ok) ?? Clean(fallback);
    }

    /// <summary>Checks a name the learner typed. Returns the cleaned name, or the reason it can't be used.</summary>
    public static (string? Name, string? Problem) Validate(string? typed, IContentFilter filter)
    {
        var name = Clean(typed ?? "");
        if (name.Length == 0) return (null, "Your project needs a name.");
        if ((typed ?? "").Trim().Length > PortfolioEntry.MaxTitleLength)
            return (null, $"Keep the name to {PortfolioEntry.MaxTitleLength} characters or fewer.");
        var check = filter.CheckText(name);
        return check.Ok ? (name, null) : (null, check.Reason);
    }

    /// <summary>One line, single spaces, no control characters, at most <see cref="PortfolioEntry.MaxTitleLength"/>.</summary>
    private static string Clean(string text)
    {
        var oneLine = Whitespace().Replace(new string(text.Where(c => !char.IsControl(c) || char.IsWhiteSpace(c)).ToArray()), " ").Trim();
        return oneLine.Length <= PortfolioEntry.MaxTitleLength ? oneLine : oneLine[..PortfolioEntry.MaxTitleLength].TrimEnd();
    }

    [GeneratedRegex(@"<title\b[^>]*>([^<]*)</title>", RegexOptions.IgnoreCase)]
    private static partial Regex TitleTag();

    /// <summary>An h1 whose content is plain text: no child elements and no JSX expressions.</summary>
    [GeneratedRegex(@"<h1\b[^>]*>([^<>{}]*)</h1>", RegexOptions.IgnoreCase)]
    private static partial Regex Heading();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>
/// Checks a portfolio picture sent by the browser. The picture is made from the learner's own page in the sandboxed
/// preview, so the server treats it as untrusted: it must really be a PNG, of a sensible size, before it's stored,
/// and it's only ever served back as <c>image/png</c> with <c>nosniff</c>.
/// </summary>
public static class PortfolioImages
{
    public const int MaxSide = 2400;
    private const string Prefix = "data:image/png;base64,";
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Decodes a <c>data:image/png;base64,…</c> URL. Returns the bytes, or a reason it was refused.</summary>
    public static (byte[]? Png, string? Problem) Read(string? dataUrl)
    {
        if (dataUrl is null || !dataUrl.StartsWith(Prefix, StringComparison.Ordinal)) return (null, "The picture must be a PNG.");

        var base64 = dataUrl.AsSpan(Prefix.Length);
        // Checked before decoding, so a huge string isn't decoded only to be thrown away.
        if (base64.Length > (PortfolioEntry.MaxScreenshotBytes + 2) / 3 * 4) return (null, "The picture is too big.");

        var bytes = new byte[base64.Length * 3 / 4];
        if (!Convert.TryFromBase64Chars(base64, bytes, out var written)) return (null, "The picture couldn't be read.");
        bytes = bytes[..written];

        // A PNG starts with its 8-byte signature, then the IHDR chunk: length 13, "IHDR", width, height.
        if (written < 33 || !bytes.AsSpan(0, 8).SequenceEqual(Signature) || bytes[12] != 'I' || bytes[13] != 'H' || bytes[14] != 'D' || bytes[15] != 'R')
            return (null, "The picture must be a PNG.");
        var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        if (width is 0 or > MaxSide || height is 0 or > MaxSide) return (null, "The picture is the wrong size.");

        return (bytes, null);
    }
}
