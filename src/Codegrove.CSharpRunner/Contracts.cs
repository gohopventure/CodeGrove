using System.Text.Json;
using System.Text.Json.Serialization;

namespace Codegrove.CSharpRunner;

/// <summary>
/// One run: the learner's files (only <c>.cs</c> files are compiled), the challenge's tests (empty for a
/// plain "Run"), and optional text for <c>Console.ReadLine</c>.
/// </summary>
public sealed record RunRequest(Dictionary<string, string> Files, List<TestSpec>? Tests = null, string? Input = null);

/// <summary>A declarative test exactly as the API sends it (see web/src/runner/types.ts).</summary>
public sealed record TestSpec(int Id, string Label, string Kind, JsonElement Spec, string FailMessage);

public sealed record TestResult(int TestId, bool Passed, string? Message);

/// <summary>A compiler error or warning in the learner's terms, plus Roslyn's own wording.</summary>
public sealed record Diagnostic(
    string File, int Line, int Column, int EndLine, int EndColumn, string Code, string Severity, string Message, string Friendly);

/// <summary>A line for the Console panel. Levels match the web app's ConsoleLine.</summary>
public sealed record ConsoleLine(string Level, string Text);

/// <summary>Where the run stopped early, if it did.</summary>
public enum StopReason { None, CompileErrors, Exception, TooManySteps, TooDeep, TooMuchOutput }

public sealed record RunResult(
    bool Compiled,
    List<Diagnostic> Diagnostics,
    List<ConsoleLine> Console,
    /// <summary>What the program printed, exactly (for tests and for a future "expected output" view).</summary>
    string Output,
    [property: JsonConverter(typeof(JsonStringEnumConverter<StopReason>))] StopReason Stopped,
    List<TestResult> Results,
    long CompileMs,
    long RunMs);

/// <summary>Source-generated JSON, so the WebAssembly build can be trimmed safely.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(RunRequest))]
[JsonSerializable(typeof(RunResult))]
public sealed partial class RunnerJson : JsonSerializerContext;
