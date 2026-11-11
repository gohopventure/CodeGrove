using System.Text;
using System.Text.RegularExpressions;

namespace Codegrove.Api.Services;

public sealed record FilterResult(bool Ok, string? Reason)
{
    public static readonly FilterResult Pass = new(true, null);
    public static FilterResult Fail(string reason) => new(false, reason);
}

public interface IContentFilter
{
    /// <summary>Checks a username: allowed characters, length, profanity and personal information.</summary>
    FilterResult CheckUsername(string userName);

    /// <summary>Checks free text another person might see (project titles, later community posts).</summary>
    FilterResult CheckText(string text);
}

/// <summary>
/// Blocklist + pattern filter. Reasons are written for a 10-year-old.
/// TRADEOFF: the built-in word list is deliberately short. Before launch, load a maintained list
/// (or call a moderation service) through the same interface; nothing else needs to change.
/// </summary>
public sealed partial class ContentFilter : IContentFilter
{
    // Matched anywhere in the normalized text.
    private static readonly string[] BlockedSubstrings =
    [
        "fuck", "shit", "bitch", "cunt", "nigg", "fag", "slut", "whore", "porn", "sex", "nazi",
    ];

    // Words matched only as whole tokens, so "class", "bass", "grape" and "skill" stay allowed.
    private static readonly HashSet<string> BlockedTokens = ["ass", "dick", "cum", "hoe", "kkk", "die", "rape", "kill"];

    public FilterResult CheckUsername(string userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
            return FilterResult.Fail("Pick a username.");

        if (!UsernameShape().IsMatch(userName))
            return FilterResult.Fail("Use 3 to 20 letters, numbers or underscores (_). No spaces.");

        if (YearLike().IsMatch(userName))
            return FilterResult.Fail("That number looks like a year. Pick a different number so your birthday stays private.");

        if (LongDigitRun().IsMatch(userName))
            return FilterResult.Fail("Use 3 numbers or fewer in a row, so it can't be a phone number.");

        return CheckText(userName);
    }

    public FilterResult CheckText(string text)
    {
        if (ContainsPersonalInfo(text))
            return FilterResult.Fail("That looks like personal information (like an email, phone number, web address or real name). Keep it secret!");

        var normalized = Normalize(text);
        if (BlockedSubstrings.Any(normalized.Contains))
            return FilterResult.Fail("That has a word we don't allow here. Try something else.");

        if (Tokens(text).Any(t => BlockedTokens.Contains(Normalize(t))))
            return FilterResult.Fail("That has a word we don't allow here. Try something else.");

        return FilterResult.Pass;
    }

    private static bool ContainsPersonalInfo(string text) =>
        Email().IsMatch(text) || Phone().IsMatch(text) || Url().IsMatch(text) || Handle().IsMatch(text)
        || StreetAddress().IsMatch(text) || NameIntro().IsMatch(text);

    /// <summary>Lowercase, fold common leetspeak, and drop everything that isn't a letter.</summary>
    internal static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var raw in text.ToLowerInvariant())
        {
            var c = raw switch
            {
                '0' => 'o', '1' => 'i', '3' => 'e', '4' => 'a', '5' => 's', '7' => 't', '8' => 'b',
                '@' => 'a', '$' => 's', '!' => 'i', '|' => 'l',
                _ => raw,
            };
            if (c is >= 'a' and <= 'z') sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Splits "CoolBass_99" into ["Cool", "Bass", "99"] (underscores, digits and camelCase).</summary>
    private static IEnumerable<string> Tokens(string text) =>
        TokenSplit().Matches(text).Select(m => m.Value);

    [GeneratedRegex(@"^[A-Za-z0-9_]{3,20}$")] private static partial Regex UsernameShape();
    [GeneratedRegex(@"(19|20)\d\d")] private static partial Regex YearLike();
    [GeneratedRegex(@"\d{4,}")] private static partial Regex LongDigitRun();
    [GeneratedRegex(@"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase)] private static partial Regex Email();
    [GeneratedRegex(@"(\+?\d[\s().-]*){7,}")] private static partial Regex Phone();
    [GeneratedRegex(@"(https?://|www\.|\.(com|net|org|io|gg|tv|me)\b)", RegexOptions.IgnoreCase)] private static partial Regex Url();
    [GeneratedRegex(@"(^|\s)@\w{2,}")] private static partial Regex Handle();
    [GeneratedRegex(@"\b\d{1,5}\s+\w+\s+(st|street|ave|avenue|rd|road|ln|lane|dr|drive|blvd|ct|court)\b", RegexOptions.IgnoreCase)] private static partial Regex StreetAddress();
    [GeneratedRegex(@"\b(my name is|i am called|i'm called|my real name)\b", RegexOptions.IgnoreCase)] private static partial Regex NameIntro();
    [GeneratedRegex(@"[A-Z]?[a-z]+|[A-Z]+(?![a-z])|\d+")] private static partial Regex TokenSplit();
}
