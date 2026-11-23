using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Codegrove.CSharpRunner;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Codegrove.WebApi.ParityTests;

/// <summary>
/// Runs a learner's program on real ASP.NET Core: compiled with Roslyn against the shared framework (with
/// the Web SDK's implicit usings, nullable off, as the runner does), started on Kestrel on a free local port
/// in Development, and stopped after use. The host is found through the "HostBuilt" diagnostic event that
/// WebApplicationFactory also relies on.
/// </summary>
internal static class RealServer
{
    /// <summary>What `dotnet new web` imports implicitly (Microsoft.NET.Sdk.Web).</summary>
    private const string WebUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Net.Http.Json;
        global using System.Threading;
        global using System.Threading.Tasks;
        global using Microsoft.AspNetCore.Builder;
        global using Microsoft.AspNetCore.Hosting;
        global using Microsoft.AspNetCore.Http;
        global using Microsoft.AspNetCore.Routing;
        global using Microsoft.Extensions.Configuration;
        global using Microsoft.Extensions.DependencyInjection;
        global using Microsoft.Extensions.Hosting;
        global using Microsoft.Extensions.Logging;
        """;

    /// <summary>
    /// The shared framework (Microsoft.NETCore.App + Microsoft.AspNetCore.App) this test process runs on, plus
    /// Entity Framework Core with its in-memory provider (lesson 6), as `dotnet add package` would give.
    /// </summary>
    private static readonly MetadataReference[] References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.Contains($"{Path.DirectorySeparatorChar}shared{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || Path.GetFileName(p).StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray();

    public static Assembly Compile(IReadOnlyDictionary<string, string> files)
    {
        var parse = new CSharpParseOptions(LanguageVersion.CSharp14);
        var trees = files.Where(f => f.Key.EndsWith(".cs", StringComparison.Ordinal))
            .Select(f => CSharpSyntaxTree.ParseText(f.Value, parse, f.Key))
            .Append(CSharpSyntaxTree.ParseText(WebUsings, parse, "__web_usings.cs"));
        var compilation = CSharpCompilation.Create("LearnerApp_" + Guid.NewGuid().ToString("N"), trees, References,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, nullableContextOptions: NullableContextOptions.Disable));
        using var pe = new MemoryStream();
        var emit = compilation.Emit(pe);
        if (!emit.Success)
            throw new InvalidOperationException("Doesn't compile against real ASP.NET Core:\n" +
                string.Join("\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        pe.Position = 0;
        return new AssemblyLoadContext(null, isCollectible: true).LoadFromStream(pe);
    }

    /// <summary>Starts the program fresh. Throws <see cref="ProgramFailedException"/> if it stops before the server starts.</summary>
    public static async Task<RunningApp> StartAsync(Assembly program)
    {
        var port = FreePort();
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", $"http://127.0.0.1:{port}");
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        Environment.SetEnvironmentVariable("Logging__LogLevel__Default", "Warning");

        // EF Core is shared by every program in this process; start each with no in-memory databases, as the runner does.
        EfReset.Reset();
        var built = HostCapture.Expect(program);
        var exited = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = program.EntryPoint!;
        var thread = new Thread(() =>
        {
            try
            {
                entry.Invoke(null, entry.GetParameters().Length == 0 ? null : [Array.Empty<string>()]);
                exited.TrySetResult(null);
            }
            catch (TargetInvocationException tie)
            {
                exited.TrySetResult(tie.InnerException);
            }
            catch (Exception ex)
            {
                exited.TrySetResult(ex);
            }
        }) { IsBackground = true, Name = "learner app" };
        thread.Start();

        var first = await Task.WhenAny(built, exited.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        if (first == exited.Task)
            throw new ProgramFailedException(exited.Task.Result ?? new InvalidOperationException("The program ended without starting a server."));
        if (first != built)
            throw new TimeoutException("The server didn't start within 30 seconds.");

        var host = await built;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        using (lifetime.ApplicationStarted.Register(() => started.TrySetResult()))
        {
            var ready = await Task.WhenAny(started.Task, exited.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            if (ready == exited.Task)
                throw new ProgramFailedException(exited.Task.Result ?? new InvalidOperationException("The program ended without starting a server."));
            if (ready != started.Task)
                throw new TimeoutException("The server didn't start within 30 seconds.");
        }
        return new RunningApp(host, thread, port);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Catches the IHost a program builds, via Microsoft.Extensions.Hosting's "HostBuilt" event.</summary>
    private static class HostCapture
    {
        private static TaskCompletionSource<IHost>? s_next;
        private static Assembly? s_program;

        static HostCapture() => DiagnosticListener.AllListeners.Subscribe(new ListenerObserver());

        /// <summary>Apps start one at a time (each test class runs its cases in order, and these are in one collection).</summary>
        public static Task<IHost> Expect(Assembly program)
        {
            var tcs = new TaskCompletionSource<IHost>(TaskCreationOptions.RunContinuationsAsynchronously);
            s_next = tcs;
            s_program = program;
            return tcs.Task;
        }

        /// <summary>
        /// MVC looks for controllers in the entry assembly, which here is the test host, not the learner's
        /// program. Add the program as an application part (what AddApplicationPart does) before the
        /// services are built, so its controllers are found as they would be in a real project.
        /// </summary>
        private static void AddProgramAsApplicationPart(IServiceCollection services)
        {
            if (s_program is null) return;
            var manager = services.FirstOrDefault(d => d.ServiceType == typeof(ApplicationPartManager))?.ImplementationInstance as ApplicationPartManager;
            if (manager is not null && !manager.ApplicationParts.OfType<AssemblyPart>().Any(p => p.Assembly == s_program))
                manager.ApplicationParts.Add(new AssemblyPart(s_program));
        }

        private sealed class ListenerObserver : IObserver<DiagnosticListener>
        {
            public void OnNext(DiagnosticListener listener)
            {
                if (listener.Name == "Microsoft.Extensions.Hosting") listener.Subscribe(new EventObserver());
            }
            public void OnCompleted() { }
            public void OnError(Exception error) { }
        }

        private sealed class EventObserver : IObserver<KeyValuePair<string, object?>>
        {
            public void OnNext(KeyValuePair<string, object?> e)
            {
                if (e.Key == "HostBuilt" && e.Value is IHost host) s_next?.TrySetResult(host);
                if (e.Key == "HostBuilding")
                {
                    if (e.Value is IHostApplicationBuilder app) AddProgramAsApplicationPart(app.Services);
                    else if (e.Value is IHostBuilder builder) builder.ConfigureServices((_, services) => AddProgramAsApplicationPart(services));
                }
            }
            public void OnCompleted() { }
            public void OnError(Exception error) { }
        }
    }
}

internal sealed class ProgramFailedException(Exception inner) : Exception(inner.Message, inner);

/// <summary>A started learner server. Dispose to stop it.</summary>
internal sealed class RunningApp(IHost host, Thread thread, int port) : IAsyncDisposable
{
    private readonly HttpClient _client = new() { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Sends a request the way the practice server receives it: a body is JSON unless a Content-Type header says otherwise.</summary>
    public async Task<HttpExchange> SendAsync(HttpRequestSpec spec)
    {
        var path = string.IsNullOrWhiteSpace(spec.Path) ? "/" : spec.Path.Trim();
        if (!path.StartsWith('/')) path = "/" + path;
        using var request = new HttpRequestMessage(new HttpMethod(spec.Method.ToUpperInvariant()), path);
        var headers = spec.Headers ?? [];
        if (spec.Body is { Length: > 0 } body)
        {
            var contentType = headers.FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value ?? "application/json";
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.Remove("Content-Type");
            request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }
        foreach (var (name, value) in headers.Where(h => !h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
            request.Headers.TryAddWithoutValidation(name, value);

        using var response = await _client.SendAsync(request);
        var all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in response.Headers) all[h.Key] = string.Join(", ", h.Value);
        foreach (var h in response.Content.Headers) all[h.Key] = string.Join(", ", h.Value);
        return new HttpExchange(spec.Method.ToUpperInvariant(), path, (int)response.StatusCode, response.ReasonPhrase ?? "",
            response.Content.Headers.ContentType?.ToString(), await response.Content.ReadAsStringAsync(), all);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            await host.StopAsync(cts.Token);
        thread.Join(TimeSpan.FromSeconds(10));
        host.Dispose();
    }
}
