using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using static Codegrove.CSharpRunner.CSharpCodeRunner;

namespace Codegrove.CSharpRunner;

/// <summary>
/// Test kinds for C# challenges (authors write them in JSON, like the web tests):
/// <list type="bullet">
/// <item><c>noErrors</c> {}: compiled, and ran to the end without an exception or being stopped.</item>
/// <item><c>consoleIncludes</c> { text, caseSensitive? }: a line of output contains the text (spaces squashed; case ignored unless asked).</item>
/// <item><c>consoleEquals</c> { lines }: the whole output is exactly these lines (trailing spaces and blank lines ignored).</item>
/// <item><c>compileError</c> { code?, contains?, line?, file? }: the compiler DOES report a matching error (lessons about reading errors).</item>
/// <item><c>csharpExpression</c> { expression, equals }: a C# expression, run after the program, against the learner's classes. Compared with the JSON value.</item>
/// <item><c>sourceMatches</c> { pattern, file?, ignoreCase?, min? }: the code (comments removed) matches a regex at least <c>min</c> times (default 1).</item>
/// </list>
/// </summary>
internal static class TestEvaluator
{
    public static readonly IReadOnlySet<string> Kinds =
        new HashSet<string> { "noErrors", "consoleIncludes", "consoleEquals", "compileError", "csharpExpression", "sourceMatches" };

    /// <summary>
    /// Evaluates every test. <paramref name="context"/> is null when the program didn't compile, so
    /// csharpExpression tests fail without trying.
    /// </summary>
    public static async Task<List<TestResult>> EvaluateAllAsync(
        List<TestSpec> tests, Dictionary<string, string> files, List<Diagnostic> diagnostics, RunOutcome outcome, RunContext? context)
    {
        var expressionTests = tests.Where(t => t.Kind == "csharpExpression").ToList();
        var expressionResults = context is null
            ? expressionTests.ToDictionary(t => t.Id, t => Fail(t, "Your code needs to compile before this can be checked."))
            : await EvaluateExpressionsAsync(expressionTests, context);
        return tests.Select(t => expressionResults.TryGetValue(t.Id, out var r) ? r : EvaluateStatic(t, files, diagnostics, outcome)).ToList();
    }

    private static TestResult EvaluateStatic(TestSpec test, Dictionary<string, string> files, List<Diagnostic> diagnostics, RunOutcome outcome)
    {
        var s = test.Spec;
        switch (test.Kind)
        {
            case "noErrors":
                return outcome.Stopped is StopReason.None or StopReason.TooMuchOutput ? Pass(test) : Fail(test);

            case "consoleIncludes":
            {
                var caseSensitive = Bool(s, "caseSensitive");
                var want = Norm(Str(s, "text"), caseSensitive);
                return Lines(outcome.Output).Any(l => Norm(l, caseSensitive).Contains(want, StringComparison.Ordinal)) ? Pass(test) : Fail(test);
            }

            case "consoleEquals":
            {
                var want = s.ValueKind == JsonValueKind.Object && s.TryGetProperty("lines", out var arr) && arr.ValueKind == JsonValueKind.Array
                    ? arr.EnumerateArray().Select(e => (e.GetString() ?? "").TrimEnd()).ToList()
                    : [];
                var got = Lines(outcome.Output).Select(l => l.TrimEnd()).ToList();
                while (got.Count > 0 && got[^1].Length == 0) got.RemoveAt(got.Count - 1);
                return got.SequenceEqual(want) ? Pass(test) : Fail(test);
            }

            case "compileError":
            {
                var code = Str(s, "code");
                var contains = Str(s, "contains");
                var file = Str(s, "file");
                var line = s.ValueKind == JsonValueKind.Object && s.TryGetProperty("line", out var l) && l.TryGetInt32(out var n) ? n : (int?)null;
                var match = diagnostics.Any(d => d.Severity == "error"
                    && (code.Length == 0 || d.Code == code)
                    && (file.Length == 0 || d.File == file)
                    && (line is null || d.Line == line)
                    && (contains.Length == 0 || d.Message.Contains(contains, StringComparison.OrdinalIgnoreCase)));
                return match ? Pass(test) : Fail(test);
            }

            case "sourceMatches":
            {
                var file = Str(s, "file");
                var min = s.ValueKind == JsonValueKind.Object && s.TryGetProperty("min", out var m) && m.TryGetInt32(out var k) ? k : 1;
                var options = RegexOptions.CultureInvariant | (Bool(s, "ignoreCase") ? RegexOptions.IgnoreCase : RegexOptions.None);
                var code = string.Join("\n", files
                    .Where(f => f.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && (file.Length == 0 || f.Key == file))
                    .Select(f => WithoutComments(f.Value)));
                var count = Regex.Matches(code, Str(s, "pattern"), options, TimeSpan.FromSeconds(1)).Count;
                return count >= min ? Pass(test) : Fail(test);
            }

            default:
                return Fail(test, $"(Unknown test kind '{test.Kind}'. This is a mistake in the lesson, not your code.)");
        }
    }

    /// <summary>
    /// Compiles every csharpExpression test into one small assembly (one method each) that can see the
    /// learner's internal types, runs them one by one, and compares each value with its <c>equals</c>.
    /// A test whose expression doesn't compile (usually: the learner hasn't written that class or method
    /// yet) fails with its own message, and the others are compiled again without it.
    /// </summary>
    private static async Task<Dictionary<int, TestResult>> EvaluateExpressionsAsync(List<TestSpec> tests, RunContext context)
    {
        var results = new Dictionary<int, TestResult>();
        if (tests.Count == 0) return results;
        if (context.Program is null || context.ProgramImage is null)
        {
            foreach (var t in tests) results[t.Id] = Fail(t, "Your code needs to compile before this can be checked.");
            return results;
        }

        var pending = tests;
        for (var attempt = 0; attempt < 2 && pending.Count > 0; attempt++)
        {
            var (source, lineRanges) = ChecksSource(pending);
            var compilation = CSharpCompilation.Create(
                ChecksAssemblyName,
                [CSharpSyntaxTree.ParseText(SourceText.From(source, System.Text.Encoding.UTF8), LearnerParseOptions, path: "__cg_checks.cs")],
                [.. References, MetadataReference.CreateFromImage(context.ProgramImage)],
                CompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var broken = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.Location.GetLineSpan().StartLinePosition.Line)
                .Select(line => lineRanges.FirstOrDefault(r => line >= r.First && line <= r.Last).Test)
                .ToHashSet();
            if (broken.Count > 0)
            {
                foreach (var t in pending.Where(broken.Contains)) results[t.Id] = Fail(t);
                // A problem outside any one test (shouldn't happen) fails them all rather than looping.
                pending = broken.Contains(null) ? [] : pending.Where(t => !broken.Contains(t)).ToList();
                if (broken.Contains(null)) foreach (var t in tests.Where(t => !results.ContainsKey(t.Id))) results[t.Id] = Fail(t);
                continue;
            }

            var (pe, _) = Emit(compilation);
            if (pe is null) break;
            var checks = context.LoadFromStream(new MemoryStream(pe)).GetType("__CgChecks")!;
            foreach (var t in pending) results[t.Id] = await RunCheckAsync(t, checks, context.Program);
            pending = [];
        }
        foreach (var t in tests.Where(t => !results.ContainsKey(t.Id))) results[t.Id] = Fail(t);
        return results;
    }

    /// <summary>One method per test: <c>T{id}()</c> returns <c>(object)(expression)</c>; awaiting inside works.</summary>
    private static (string Source, List<(int First, int Last, TestSpec? Test)> Lines) ChecksSource(List<TestSpec> tests)
    {
        var lines = new List<string>
        {
            "global using System;", "global using System.Collections.Generic;", "global using System.Linq;", "global using System.Threading.Tasks;",
            "public static class __CgChecks", "{",
        };
        var ranges = new List<(int, int, TestSpec?)>();
        foreach (var t in tests)
        {
            var first = lines.Count;
            lines.Add($"    public static async System.Threading.Tasks.Task<object> T{t.Id}()");
            lines.Add("    {");
            lines.Add("        await System.Threading.Tasks.Task.CompletedTask;");
            lines.Add("        return (object)(");
            lines.AddRange(Str(t.Spec, "expression").Replace("\r", "").Split('\n'));
            lines.Add("        );");
            lines.Add("    }");
            ranges.Add((first, lines.Count - 1, t));
        }
        lines.Add("}");
        return (string.Join("\n", lines), ranges);
    }

    private static async Task<TestResult> RunCheckAsync(TestSpec test, Type checks, System.Reflection.Assembly program)
    {
        ResetGuard(program);
        object? value;
        try
        {
            value = await (Task<object>)checks.GetMethod($"T{test.Id}")!.Invoke(null, null)!;
        }
        catch (Exception ex)
        {
            var inner = ex is System.Reflection.TargetInvocationException { InnerException: { } i } ? i : ex;
            if (GuardStopped(program) != StopReason.None)
                return Fail(test, "(Codegrove had to stop your code while checking it: a loop or method kept going.)");
            // Short form: the full, plain-language message is already in the Output panel when the program hit it too.
            var at = GuardLocation(program) is { } loc ? $" in {loc.File}, line {loc.Line}" : "";
            return Fail(test, $"(While checking, your code stopped with {inner.GetType().Name}{at}.)");
        }

        var expected = test.Spec.ValueKind == JsonValueKind.Object && test.Spec.TryGetProperty("equals", out var e) ? e : default;
        return Matches(value, expected) ? Pass(test) : Fail(test, $"(It was {Show(value)}.)");
    }

    /// <summary>Compares a C# value with a JSON value: strings, numbers (within 1e-9), booleans, null, arrays.</summary>
    internal static bool Matches(object? value, JsonElement expected) => expected.ValueKind switch
    {
        JsonValueKind.Undefined => value is true, // no "equals": the expression should be true
        JsonValueKind.Null => value is null,
        JsonValueKind.True => value is true,
        JsonValueKind.False => value is false,
        JsonValueKind.String => value is not null && Convert.ToString(value, CultureInfo.InvariantCulture) == expected.GetString(),
        JsonValueKind.Number => IsNumber(value) && Math.Abs(Convert.ToDouble(value, CultureInfo.InvariantCulture) - expected.GetDouble()) < 1e-9,
        JsonValueKind.Array => value is IEnumerable items and not string
            && items.Cast<object?>().ToList() is var list
            && list.Count == expected.GetArrayLength()
            && list.Zip(expected.EnumerateArray()).All(p => Matches(p.First, p.Second)),
        _ => false,
    };

    private static bool IsNumber(object? v) => v is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal;

    private static string Show(object? value) => value switch
    {
        null => "null",
        string s => $"\"{s}\"",
        bool b => b ? "true" : "false",
        IEnumerable e => "[" + string.Join(", ", e.Cast<object?>().Take(10).Select(Show)) + "]",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "?",
    };

    private static string WithoutComments(string code)
    {
        var root = CSharpSyntaxTree.ParseText(code, LearnerParseOptions).GetRoot();
        var comments = root.DescendantTrivia().Where(t =>
            t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia)
            || t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));
        return root.ReplaceTrivia(comments, (_, _) => SyntaxFactory.Space).ToFullString();
    }

    private static IEnumerable<string> Lines(string output) => output.Replace("\r", "").Split('\n');

    private static string Norm(string text, bool caseSensitive)
    {
        var squashed = Regex.Replace(text, @"\s+", " ").Trim();
        return caseSensitive ? squashed : squashed.ToLowerInvariant();
    }

    private static string Str(JsonElement s, string name) =>
        s.ValueKind == JsonValueKind.Object && s.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static bool Bool(JsonElement s, string name) =>
        s.ValueKind == JsonValueKind.Object && s.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static TestResult Pass(TestSpec t) => new(t.Id, true, null);

    private static TestResult Fail(TestSpec t, string? extra = null) =>
        new(t.Id, false, extra is null ? t.FailMessage : $"{t.FailMessage} {extra}");
}
