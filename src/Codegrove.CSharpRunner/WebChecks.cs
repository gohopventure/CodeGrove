using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using static Codegrove.CSharpRunner.CSharpCodeRunner;

namespace Codegrove.CSharpRunner;

/// <summary>
/// The <c>httpRequest</c> test kind: start the learner's server fresh, send it a request, compare the answer.
/// <code>
/// { "method": "POST", "path": "/pets", "body": { "name": "Moss" },
///   "before": [ { "method": "POST", "path": "/pets", "body": { … } } ],   // sent first, answers not checked
///   "expectStatus": 201,
///   "expectJson": { "name": "Moss" },          // partial: listed properties must match, others may exist
///   "expectBody": "Hello!",                    // exact text (trailing whitespace ignored)
///   "expectBodyIncludes": "hello",             // text contains this (case ignored)
///   "expectContentType": "application/json",   // Content-Type starts with this
///   "expectHeader": { "Location": "/pets/4" }  // "*" = the header just has to be there
/// }
/// </code>
/// Every check gets a server in its starting state, so checks can't affect each other.
/// </summary>
internal static class WebChecks
{
    public const string Kind = "httpRequest";

    public static async Task<Dictionary<int, TestResult>> EvaluateAsync(List<TestSpec> tests, RunContext? context)
    {
        var results = new Dictionary<int, TestResult>();
        foreach (var t in tests)
            results[t.Id] = context?.Program is null
                ? Fail(t, "Your code needs to compile before this can be checked.")
                : await EvaluateAsync(t, context.Program);
        return results;
    }

    private static async Task<TestResult> EvaluateAsync(TestSpec test, System.Reflection.Assembly program)
    {
        var (app, why) = await WebServer.StartFreshAsync(program);
        if (app is null) return Fail(test, why);

        var s = test.Spec;
        if (s.TryGetProperty("before", out var before) && before.ValueKind == JsonValueKind.Array)
        {
            foreach (var step in before.EnumerateArray())
            {
                var (setup, setupProblem) = await WebServer.SendAsync(app, program, RequestFrom(step));
                if (setupProblem is not null)
                    return Fail(test, $"(Getting ready for this check, {setup.Method} {setup.Path} answered {setup.Status} {setup.Reason}.)");
            }
        }

        var (got, problem) = await WebServer.SendAsync(app, program, RequestFrom(s));
        var mismatch = Mismatch(s, got);
        if (mismatch is null) return new(test.Id, true, null);
        var crash = problem is not null && got.Status == 500 ? $" {ShortCrash(problem)}" : "";
        return Fail(test, $"(It answered {Describe(got)}{crash})");
    }

    internal static HttpRequestSpec RequestFrom(JsonElement s)
    {
        var method = Str(s, "method") is { Length: > 0 } m ? m : "GET";
        var path = Str(s, "path") is { Length: > 0 } p ? p : "/";
        string? body = null;
        if (s.TryGetProperty("body", out var b))
            body = b.ValueKind == JsonValueKind.String ? b.GetString() : b.GetRawText();
        Dictionary<string, string>? headers = null;
        if (s.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
            headers = h.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        return new(method, path, body, headers);
    }

    /// <summary>Null when the response is what the check expects; otherwise which part differs.</summary>
    internal static string? Mismatch(JsonElement s, HttpExchange got)
    {
        if (s.TryGetProperty("expectStatus", out var st) && st.TryGetInt32(out var status) && got.Status != status) return "status";

        if (Str(s, "expectContentType") is { Length: > 0 } ct
            && !(got.ContentType ?? "").StartsWith(ct, StringComparison.OrdinalIgnoreCase)) return "content type";

        if (s.TryGetProperty("expectHeader", out var eh) && eh.ValueKind == JsonValueKind.Object)
        {
            foreach (var h in eh.EnumerateObject())
            {
                if (!got.Headers.TryGetValue(h.Name, out var value)) return "header";
                var want = h.Value.ToString();
                if (want != "*" && !string.Equals(value, want, StringComparison.Ordinal)) return "header";
            }
        }

        if (s.TryGetProperty("expectBody", out var eb) && eb.ValueKind == JsonValueKind.String
            && got.Body.TrimEnd() != (eb.GetString() ?? "").TrimEnd()) return "body";

        if (Str(s, "expectBodyIncludes") is { Length: > 0 } inc
            && !got.Body.Contains(inc, StringComparison.OrdinalIgnoreCase)) return "body";

        if (s.TryGetProperty("expectJson", out var ej))
        {
            JsonElement actual;
            try
            {
                using var doc = JsonDocument.Parse(got.Body);
                actual = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return "json";
            }
            if (!JsonMatches(ej, actual, Bool(s, "anyOrder"))) return "json";
        }
        return null;
    }

    /// <summary>
    /// Partial match: every property in <paramref name="want"/> must be in <paramref name="got"/> with a matching
    /// value; extra properties are fine. Arrays must have the same length (in order, unless anyOrder).
    /// </summary>
    internal static bool JsonMatches(JsonElement want, JsonElement got, bool anyOrder = false)
    {
        switch (want.ValueKind)
        {
            case JsonValueKind.Object:
                if (got.ValueKind != JsonValueKind.Object) return false;
                foreach (var p in want.EnumerateObject())
                    if (!got.TryGetProperty(p.Name, out var g) || !JsonMatches(p.Value, g, anyOrder)) return false;
                return true;
            case JsonValueKind.Array:
                if (got.ValueKind != JsonValueKind.Array || got.GetArrayLength() != want.GetArrayLength()) return false;
                var gotItems = got.EnumerateArray().ToList();
                if (!anyOrder) return want.EnumerateArray().Zip(gotItems).All(x => JsonMatches(x.First, x.Second, anyOrder));
                foreach (var w in want.EnumerateArray())
                {
                    var i = gotItems.FindIndex(g => JsonMatches(w, g, anyOrder));
                    if (i < 0) return false;
                    gotItems.RemoveAt(i);
                }
                return true;
            case JsonValueKind.Number:
                return got.ValueKind == JsonValueKind.Number && Math.Abs(got.GetDouble() - want.GetDouble()) < 1e-9;
            case JsonValueKind.String:
                return got.ValueKind == JsonValueKind.String && got.GetString() == want.GetString();
            default:
                return got.ValueKind == want.ValueKind;
        }
    }

    /// <summary>"404 Not Found with no body." / "200 OK with {"name":"Moss"}." (long bodies shortened)</summary>
    internal static string Describe(HttpExchange got)
    {
        var status = $"{got.Status} {got.Reason}".TrimEnd();
        if (got.Body.Length == 0) return $"{status} with no body.";
        var body = got.Body.ReplaceLineEndings(" ");
        if (body.Length > 160) body = body[..157] + "…";
        var kind = (got.ContentType ?? "").StartsWith("text/plain", StringComparison.OrdinalIgnoreCase) ? "the text " : "";
        // "with the text Let's race!" rather than "Let's race!."
        return $"{status} with {kind}{body}{(body.EndsWith('.') || body.EndsWith('!') || body.EndsWith('?') ? "" : ".")}";
    }

    private static string ShortCrash(string problem)
    {
        // "GET /x crashed, so the server answered 500 Internal Server Error. NullReferenceException in Program.cs, line 8: …"
        var at = problem.IndexOf(". ", StringComparison.Ordinal);
        var detail = at < 0 ? problem : problem[(at + 2)..];
        var colon = detail.IndexOf(':');
        return $"Your endpoint stopped with {(colon < 0 ? detail : detail[..colon])}.";
    }

    private static string Str(JsonElement s, string name) =>
        s.ValueKind == JsonValueKind.Object && s.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static bool Bool(JsonElement s, string name) =>
        s.ValueKind == JsonValueKind.Object && s.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static TestResult Fail(TestSpec t, string? extra) =>
        new(t.Id, false, extra is null ? t.FailMessage : $"{t.FailMessage} {extra}");
}
