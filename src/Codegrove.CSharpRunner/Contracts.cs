using System.Text.Json;
using System.Text.Json.Serialization;

namespace Codegrove.CSharpRunner;

/// <summary>
/// One run: the learner's files (only <c>.cs</c> files are compiled), the challenge's tests (empty for a
/// plain "Run"), and optional text for <c>Console.ReadLine</c>.
/// </summary>
public sealed record RunRequest(
    Dictionary<string, string> Files,
    List<TestSpec>? Tests = null,
    string? Input = null,
    /// <summary>For a program that starts a web server: requests to send it, in order, after it starts (the Try it panel).</summary>
    List<HttpRequestSpec>? Requests = null,
    /// <summary>
    /// Keep the server running after the run (the capstone's live API), so the preview page can send it
    /// requests one at a time with <see cref="LiveRequest"/>. Its in-memory database keeps its rows between
    /// requests, as a real running server's would. The next run stops it.
    /// </summary>
    bool KeepServer = false);

/// <summary>One request for the learner's server. A body is sent as JSON unless a Content-Type header says otherwise.</summary>
public sealed record HttpRequestSpec(string Method, string Path, string? Body = null, Dictionary<string, string>? Headers = null);

/// <summary>What the learner's server answered.</summary>
public sealed record HttpExchange(string Method, string Path, int Status, string Reason, string? ContentType, string Body, Dictionary<string, string> Headers);

/// <summary>The learner's web server after the program ran: whether app.Run() was reached, and its endpoints ("GET /hello").</summary>
public sealed record ServerInfo(bool Running, string Url, List<string> Endpoints);

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
    long RunMs,
    /// <summary>Set when the program built a web app (the Web API course).</summary>
    ServerInfo? Server = null,
    /// <summary>Answers to <see cref="RunRequest.Requests"/>, in the same order.</summary>
    List<HttpExchange>? Responses = null,
    /// <summary>Set when <see cref="RunRequest.KeepServer"/> kept the server running: pass it to <see cref="LiveRequest"/>.</summary>
    string? LiveSession = null);

/// <summary>One request for the server a <see cref="RunRequest.KeepServer"/> run left running.</summary>
public sealed record LiveRequest(string Session, HttpRequestSpec Request);

/// <summary>
/// The live server's answer. <paramref name="Found"/> is false when that server isn't running any more
/// (a newer run replaced it, or the worker restarted): run the program again to start a new one.
/// <paramref name="Console"/> holds what the endpoint printed and any crash explained in plain words.
/// </summary>
public sealed record LiveResponse(bool Found, HttpExchange? Exchange, List<ConsoleLine> Console);

/// <summary>Source-generated JSON, so the WebAssembly build can be trimmed safely.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(RunRequest))]
[JsonSerializable(typeof(RunResult))]
[JsonSerializable(typeof(LiveRequest))]
[JsonSerializable(typeof(LiveResponse))]
public sealed partial class RunnerJson : JsonSerializerContext;
