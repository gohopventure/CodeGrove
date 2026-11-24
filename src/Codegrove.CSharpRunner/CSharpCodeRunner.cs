using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Codegrove.CSharpRunner;

/// <summary>
/// Compiles the learner's .cs files, runs the program with its console captured, then runs the
/// challenge's tests. Single-threaded on purpose: the browser build has one thread.
///
/// Flow: compile as written (errors and warnings, line numbers exactly as in the editor) → if it
/// compiled, add the loop/recursion guard and compile again → load into a fresh load context → run →
/// evaluate tests (some compile small check expressions against the learner's types).
/// </summary>
public static class CSharpCodeRunner
{
    internal const string ProgramAssemblyName = "CgProgram";
    internal const string ChecksAssemblyName = "CgChecks";
    private const int MaxConsoleErrors = 8;

    internal static readonly CSharpParseOptions LearnerParseOptions = new(LanguageVersion.CSharp14);

    /// <summary>
    /// The usings `dotnet new console` would give, minus network APIs (System.Net.Http isn't referenced). Learners can still
    /// write their own `using` lines; duplicates are harmless.
    /// </summary>
    private const string GlobalUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.Linq;
        global using System.Threading.Tasks;
        [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("CgChecks")]
        """;

    /// <summary>
    /// The extra usings a `dotnet new web` project gets (Microsoft.NET.Sdk.Web), for programs that build a
    /// web app, so Web API lessons look exactly like a real project. Only the namespaces the practice
    /// server has; System.Net.Http stays out.
    /// </summary>
    private const string WebGlobalUsings = """
        global using Microsoft.AspNetCore.Builder;
        global using Microsoft.AspNetCore.Http;
        global using Microsoft.AspNetCore.Routing;
        global using Microsoft.Extensions.DependencyInjection;
        """;

    /// <summary>Whether the learner's program is a web server (Web API course): it mentions WebApplication.</summary>
    internal static bool IsWebProgram(IEnumerable<string> sources) =>
        sources.Any(s => s.Contains("WebApplication", StringComparison.Ordinal));

    private static ImmutableArray<MetadataReference> s_references;

    /// <summary>The allow-listed reference assemblies embedded in this library (see the .csproj).</summary>
    internal static ImmutableArray<MetadataReference> References
    {
        get
        {
            if (!s_references.IsDefault) return s_references;
            var asm = typeof(CSharpCodeRunner).Assembly;
            var refs = asm.GetManifestResourceNames()
                .Where(n => n.StartsWith("ref/", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .Select(n =>
                {
                    using var stream = asm.GetManifestResourceStream(n)!;
                    using var copy = new MemoryStream();
                    stream.CopyTo(copy);
                    return (MetadataReference)MetadataReference.CreateFromImage(copy.ToArray(), filePath: n["ref/".Length..]);
                })
                .ToImmutableArray();
            if (refs.Length == 0) throw new InvalidOperationException("No reference assemblies are embedded in the C# runner.");
            return s_references = refs;
        }
    }

    internal static CSharpCompilationOptions CompilationOptions(OutputKind kind) => new(
        kind,
        optimizationLevel: OptimizationLevel.Debug,
        concurrentBuild: false, // one thread in the browser
        deterministic: true,
        nullableContextOptions: NullableContextOptions.Disable, // nullable warnings confuse beginners; a later Builder lesson can turn them on
        reportSuppressedDiagnostics: false);

    /// <summary>Compiles a tiny program, so the first real run doesn't pay Roslyn's start-up cost.</summary>
    public static Task WarmUpAsync() =>
        RunAsync(new RunRequest(new() { ["Program.cs"] = "Console.WriteLine(1 + 1);" }));

    /// <summary>Sends one request to the server a <see cref="RunRequest.KeepServer"/> run left running.</summary>
    public static Task<LiveResponse> SendToLiveServerAsync(LiveRequest request) => LiveServer.SendAsync(request);

    /// <summary>Stops the live server, if one is running.</summary>
    public static void StopLiveServer() => LiveServer.Stop();

    public static async Task<RunResult> RunAsync(RunRequest request)
    {
        // A run resets EF Core's in-memory databases, so the previous live server can't outlive it.
        LiveServer.Stop();
        var compileWatch = Stopwatch.StartNew();
        var sources = request.Files
            .Where(f => f.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !f.Key.StartsWith('.'))
            .ToList();
        var tests = request.Tests ?? [];
        var trees = sources
            .Select(f => CSharpSyntaxTree.ParseText(SourceText.From(f.Value, Encoding.UTF8), LearnerParseOptions, path: f.Key))
            .ToList();

        var isWeb = IsWebProgram(sources.Select(f => f.Value));
        List<SyntaxTree> generated = [Generated(GlobalUsings, "__cg_usings.cs"), Generated(Guard.Source, "__cg_guard.cs")];
        if (isWeb) generated.Add(Generated(WebGlobalUsings, "__cg_web_usings.cs"));
        var compilation = CSharpCompilation.Create(
            ProgramAssemblyName,
            [.. trees, .. generated],
            References,
            CompilationOptions(OutputKind.ConsoleApplication));

        var diagnostics = compilation.GetDiagnostics()
            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning && !d.IsSuppressed)
            .Select(ToDiagnostic)
            .Where(d => !d.File.StartsWith("__cg", StringComparison.Ordinal))
            .OrderBy(d => d.Severity == "error" ? 0 : 1).ThenBy(d => d.File).ThenBy(d => d.Line).ThenBy(d => d.Column)
            .ToList();
        var errors = diagnostics.Where(d => d.Severity == "error").ToList();
        var console = new List<ConsoleLine>();

        if (errors.Count > 0)
        {
            // One missing type can cause the same error on many lines (seven "can't find Card"s in a
            // starter). The console shows each distinct error once and lists its other lines; the
            // editor markers still come from the full diagnostics list.
            var groups = errors.GroupBy(e => (e.File, e.Code, e.Message)).ToList();
            foreach (var g in groups.Take(MaxConsoleErrors)) console.Add(new("error", DescribeCompileErrors(g.ToList())));
            if (groups.Count > MaxConsoleErrors) console.Add(new("error", $"…and {groups.Count - MaxConsoleErrors} more errors. Fix the first one, then check again: one mistake often causes several errors."));
            var notRun = new RunOutcome(null, "", StopReason.CompileErrors, null);
            return new RunResult(false, diagnostics, console, "", StopReason.CompileErrors,
                await TestEvaluator.EvaluateAllAsync(tests, request.Files, diagnostics, notRun, null),
                compileWatch.ElapsedMilliseconds, 0);
        }

        // Guarded build for running. Same trees, same line numbers.
        var guarded = compilation;
        foreach (var tree in trees)
        {
            var root = new GuardRewriter(tree.FilePath).Visit(tree.GetRoot());
            guarded = guarded.ReplaceSyntaxTree(tree, tree.WithRootAndOptions(root, tree.Options));
        }
        var (pe, emitErrors) = Emit(guarded);
        compileWatch.Stop();
        if (pe is null)
        {
            // The guard should never break a program that compiled. If it does, say so plainly.
            console.Add(new("error", "Codegrove couldn't prepare your program to run (this is our bug, not yours). " + string.Join(" ", emitErrors)));
            var broken = new RunOutcome(null, "", StopReason.CompileErrors, null);
            return new RunResult(false, diagnostics, console, "", StopReason.CompileErrors,
                await TestEvaluator.EvaluateAllAsync(tests, request.Files, diagnostics, broken, null),
                compileWatch.ElapsedMilliseconds, 0);
        }

        // Warnings don't stop the program, but they're often the lesson (nullable warnings especially),
        // so they're shown above the output as well as underlined in the editor.
        var warnings = diagnostics.Where(d => d.Severity == "warning").GroupBy(d => (d.File, d.Code, d.Message)).ToList();
        foreach (var g in warnings.Take(MaxConsoleErrors)) console.Add(new("warn", DescribeCompileErrors(g.ToList())));
        if (warnings.Count > MaxConsoleErrors) console.Add(new("warn", $"…and {warnings.Count - MaxConsoleErrors} more warnings."));

        var runWatch = Stopwatch.StartNew();
        var context = RunContext.Create();
        var kept = false;
        try
        {
            var program = context.LoadFromStream(new MemoryStream(pe));
            context.Program = program;
            context.ProgramImage = pe;
            EfReset.Reset(); // EF Core lives outside the run's load context; see EfReset
            var slot = Codegrove.WebShim.ShimHost.Begin();
            RunOutcome outcome;
            try
            {
                outcome = await RunProgramAsync(program, request.Input, console);
            }
            finally
            {
                Codegrove.WebShim.ShimHost.End();
            }
            var (server, responses) = await WebServer.AfterRunAsync(slot, outcome, program, request.Requests, console);
            var results = await TestEvaluator.EvaluateAllAsync(tests, request.Files, diagnostics, outcome, context);

            string? liveSession = null;
            if (request.KeepServer && outcome.Stopped == StopReason.None && slot.Running is { } running)
            {
                // httpRequest checks each started (and reset) their own copy of the server, emptying the
                // in-memory database the first one was using, so the live server is a fresh start too.
                var live = running;
                if (tests.Any(t => t.Kind == WebChecks.Kind))
                    live = (await WebServer.StartFreshAsync(program)).App ?? running;
                ResetGuard(program);
                liveSession = LiveServer.Keep(context, program, live);
                kept = true;
            }
            runWatch.Stop();
            return new RunResult(true, diagnostics, console, outcome.Output, outcome.Stopped, results,
                compileWatch.ElapsedMilliseconds, runWatch.ElapsedMilliseconds, server, responses, liveSession);
        }
        finally
        {
            if (!kept) context.Dispose();
        }
    }

    private static SyntaxTree Generated(string source, string path) =>
        CSharpSyntaxTree.ParseText(SourceText.From(source, Encoding.UTF8), LearnerParseOptions, path: path);

    /// <summary>
    /// Emits without a PDB on purpose: Roslyn's PDB writer waits on a background task, which single-threaded
    /// WebAssembly can't do ("Cannot wait on monitors"). Line numbers for runtime errors come from the guard
    /// instead (<see cref="GuardRewriter"/> records the file and line of each statement as it runs).
    /// </summary>
    internal static (byte[]? Pe, string[] Errors) Emit(CSharpCompilation compilation)
    {
        using var pe = new MemoryStream();
        var result = compilation.Emit(pe);
        return result.Success
            ? (pe.ToArray(), [])
            : (null, result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).ToArray());
    }

    internal static Diagnostic ToDiagnostic(Microsoft.CodeAnalysis.Diagnostic d)
    {
        var span = d.Location.GetLineSpan();
        return new Diagnostic(
            span.Path ?? "",
            span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1,
            span.EndLinePosition.Line + 1, span.EndLinePosition.Character + 1,
            d.Id,
            d.Severity == DiagnosticSeverity.Error ? "error" : "warning",
            d.GetMessage(System.Globalization.CultureInfo.InvariantCulture),
            FriendlyErrors.ForCompilerError(d.Id, d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>"Error in Program.cs, line 3: [friendly] (CS1002: ; expected)", or "Warning in …" for a warning.</summary>
    internal static string DescribeCompileError(Diagnostic e, string alsoOn = "")
    {
        var kind = e.Severity == "warning" ? "Warning" : "Error";
        return string.IsNullOrEmpty(e.Friendly)
            ? $"{kind} in {e.File}, line {e.Line}: {e.Message}{alsoOn} ({e.Code})"
            : $"{kind} in {e.File}, line {e.Line}: {e.Friendly}{alsoOn} ({e.Code}: {e.Message})";
    }

    /// <summary>One error or warning, plus "The same error is on lines 4, 5 and 11 too." when it repeats in that file.</summary>
    internal static string DescribeCompileErrors(IReadOnlyList<Diagnostic> sameError)
    {
        var others = sameError.Skip(1).Select(e => e.Line).Where(l => l != sameError[0].Line).Distinct().ToList();
        if (others.Count == 0) return DescribeCompileError(sameError[0]);
        var lines = others.Count == 1
            ? $"line {others[0]}"
            : $"lines {string.Join(", ", others.Take(others.Count - 1))} and {others[^1]}";
        // Before the technical part in brackets, so it's read with the plain-language part.
        var what = sameError[0].Severity == "warning" ? "warning" : "error";
        return DescribeCompileError(sameError[0], $" The same {what} is on {lines} too.");
    }

    /// <summary>What happened when the program ran. <paramref name="Error"/> is the learner's exception, if any.</summary>
    internal sealed record RunOutcome(Assembly? Program, string Output, StopReason Stopped, Exception? Error);

    internal static async Task<RunOutcome> RunProgramAsync(Assembly program, string? input, List<ConsoleLine> console)
    {
        var capture = new ConsoleCapture(console);
        // Console.In can't even be read in the browser, so it isn't saved and restored: it's left empty after the run.
        var (oldOut, oldErr) = (Console.Out, Console.Error);
        Console.SetOut(capture.Out);
        Console.SetError(capture.Error);
        Console.SetIn(new StringReader(input ?? ""));
        Exception? error = null;
        try
        {
            var entry = AsyncEntryPoint(program) ?? program.EntryPoint ?? throw new InvalidOperationException("The program has no entry point.");
            var args = entry.GetParameters().Length == 0 ? null : new object[] { Array.Empty<string>() };
            var returned = entry.Invoke(null, args);
            if (returned is Task task) await task;
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            error = tie.InnerException;
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            // app.Run() ends the program on purpose (a real server waits there): not an error.
            if (error is Codegrove.WebShim.ServerStartedSignal) error = null;
            capture.Flush();
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
            Console.SetIn(TextReader.Null);
        }

        var stopped = GuardStopped(program);
        if (stopped != StopReason.None)
        {
            console.Add(new("error", stopped == StopReason.TooDeep
                ? $"Your program went more than {Guard.MaxDepth:N0} method calls deep, so Codegrove stopped it. Does a method keep calling itself with no way to stop?"
                : $"Your program ran more than {Guard.MaxSteps / 1_000_000} million loop steps, so Codegrove stopped it. Is there a loop that never ends? Check that its condition becomes false."));
            return new RunOutcome(program, capture.Output, stopped, null);
        }
        if (error is not null)
        {
            console.Add(new("error", DescribeException(error, program)));
            return new RunOutcome(program, capture.Output, StopReason.Exception, error);
        }
        if (capture.Truncated)
        {
            console.Add(new("info", "Your program printed a lot, so only the first part is shown."));
            return new RunOutcome(program, capture.Output, StopReason.TooMuchOutput, null);
        }
        return new RunOutcome(program, capture.Output, StopReason.None, null);
    }

    /// <summary>
    /// For an async program (top-level <c>await</c>, or <c>async Task Main</c>) the real entry point is a small
    /// bridge, <c>&lt;Main&gt;</c>, that blocks until the async method finishes. Blocking isn't possible in the
    /// browser, so the async method itself is called and awaited instead.
    /// </summary>
    private static MethodInfo? AsyncEntryPoint(Assembly program)
    {
        var bridge = program.EntryPoint;
        if (bridge is null || bridge.Name != "<Main>") return null;
        const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        return bridge.DeclaringType?.GetMethods(Static).FirstOrDefault(m =>
            m.Name is "<Main>$" or "Main" && m != bridge && typeof(Task).IsAssignableFrom(m.ReturnType));
    }

    /// <summary>Reads __CgGuard.Stopped from the learner's assembly ("steps", "depth" or null).</summary>
    internal static StopReason GuardStopped(Assembly program) =>
        program.GetType("__CgGuard")?.GetField("Stopped")?.GetValue(null) switch
        {
            "steps" => StopReason.TooManySteps,
            "depth" => StopReason.TooDeep,
            _ => StopReason.None,
        };

    internal static void ResetGuard(Assembly program) =>
        program.GetType("__CgGuard")?.GetMethod("Reset")?.Invoke(null, null);

    /// <summary>"NullReferenceException in Program.cs, line 7: [friendly] (the real message)".</summary>
    internal static string DescribeException(Exception ex, Assembly program)
    {
        var name = ex.GetType().Name;
        var where = GuardLocation(program) is { } at ? $" in {at.File}, line {at.Line}" : "";
        var friendly = FriendlyErrors.ForException(name);
        return friendly.Length > 0
            ? $"{name}{where}: {friendly} ({ex.Message})"
            : $"{name}{where}: {ex.Message}";
    }

    /// <summary>The file and line of the statement that was running last (where an exception came from).</summary>
    internal static (string File, int Line)? GuardLocation(Assembly program)
    {
        var guard = program.GetType("__CgGuard");
        var file = guard?.GetField("File")?.GetValue(null) as string;
        var line = guard?.GetField("Line")?.GetValue(null) as int? ?? 0;
        return file is not null && line > 0 ? (file, line) : null;
    }

    /// <summary>
    /// A load context per run, so each run starts with fresh static fields. Collectible where the runtime
    /// supports it (desktop .NET); in the browser, Mono keeps the small assemblies until the worker is recycled.
    /// </summary>
    internal sealed class RunContext : AssemblyLoadContext, IDisposable
    {
        private RunContext(bool collectible) : base("cg-run", collectible) { }

        public Assembly? Program { get; set; }
        public byte[]? ProgramImage { get; set; }

        public static RunContext Create()
        {
            try
            {
                return new RunContext(collectible: true);
            }
            catch (NotSupportedException)
            {
                return new RunContext(collectible: false);
            }
        }

        // The checks assembly references the learner's program by name.
        protected override Assembly? Load(AssemblyName name) =>
            name.Name == ProgramAssemblyName ? Program : null;

        public void Dispose()
        {
            if (IsCollectible) Unload();
        }
    }
}
