using System.Text.Json;
using Codegrove.CSharpRunner;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Codegrove.WebApi.ParityTests;

/// <summary>Comparing the practice server's answers with real ASP.NET Core's.</summary>
internal static class Parity
{
    /// <summary>
    /// Null when the two answers agree; otherwise what differs. They agree when the status and media type
    /// match; JSON bodies match where the practice server has a value (real ASP.NET Core may add fields, like
    /// traceId); plain-text error bodies match on their first line (the real one is the developer exception
    /// page, which goes on with a stack trace, and starts with the exception type); other bodies match
    /// exactly; and Location and Allow headers match when the practice server sends them.
    /// </summary>
    public static string? Diff(HttpExchange shim, HttpExchange real)
    {
        var problems = new List<string>();
        if (shim.Status != real.Status) problems.Add($"status {shim.Status} vs real {real.Status}");
        if (MediaType(shim.ContentType) != MediaType(real.ContentType))
            problems.Add($"content type '{shim.ContentType}' vs real '{real.ContentType}'");

        if (TryJson(shim.Body, out var shimJson) && TryJson(real.Body, out var realJson))
        {
            if (!WebChecks.JsonMatches(shimJson, realJson)) problems.Add($"JSON {shim.Body} vs real {real.Body}");
        }
        else if (shim.Status >= 400 && MediaType(shim.ContentType) == "text/plain")
        {
            var shimLine = FirstLine(shim.Body);
            var realLine = FirstLine(real.Body);
            if (!realLine.Contains(shimLine, StringComparison.Ordinal)) problems.Add($"error text '{shimLine}' vs real '{realLine}'");
        }
        else if (shim.Body != real.Body)
        {
            problems.Add($"body '{Short(shim.Body)}' vs real '{Short(real.Body)}'");
        }

        foreach (var header in new[] { "Location", "Allow" })
        {
            if (shim.Headers.TryGetValue(header, out var mine) && !(real.Headers.TryGetValue(header, out var theirs) && Same(header, mine, theirs)))
                problems.Add($"{header} '{mine}' vs real '{real.Headers.GetValueOrDefault(header)}'");
        }
        return problems.Count == 0 ? null : $"{shim.Method} {shim.Path}: " + string.Join("; ", problems);
    }

    /// <summary>Allow lists methods; their order isn't part of the meaning.</summary>
    private static bool Same(string header, string mine, string theirs) => header != "Allow"
        ? mine == theirs
        : Methods(mine).SetEquals(Methods(theirs));

    private static HashSet<string> Methods(string allow) => allow.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    private static string MediaType(string? contentType) => (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();

    private static string FirstLine(string body) => body.Split('\n')[0].TrimEnd('\r');

    private static string Short(string body) => body.Length > 200 ? body[..200] + "…" : body;

    private static bool TryJson(string body, out JsonElement json)
    {
        json = default;
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            json = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
