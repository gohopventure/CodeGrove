using System.Reflection;
using Microsoft.AspNetCore.Builder;
using static Codegrove.CSharpRunner.CSharpCodeRunner;

namespace Codegrove.CSharpRunner;

/// <summary>
/// The capstone's live API: the learner's server, kept running after a run so the React preview can call
/// it with fetch('/api/…') one request at a time.
/// </summary>
/// <remarks>
/// <para>One live server at a time. Every run stops the previous one first, because a run resets EF Core's
/// in-memory databases (<see cref="EfReset"/>), which would empty the live server's tables under it.</para>
/// <para>Each request gets its own step budget and depth limit (<see cref="WebServer.SendAsync"/>), and
/// host.js's time limit applies to each request as it does to a run. If the worker is restarted, the
/// session is gone and <see cref="SendAsync"/> answers Found = false; the app starts a new server.</para>
/// </remarks>
internal static class LiveServer
{
    private sealed record Session(string Id, RunContext Context, Assembly Program, WebApplication App);

    private static Session? s_current;

    /// <summary>The id of the running session, if any (for tests).</summary>
    internal static string? CurrentId => s_current?.Id;

    /// <summary>Keeps <paramref name="app"/> running and takes ownership of its load context.</summary>
    public static string Keep(RunContext context, Assembly program, WebApplication app)
    {
        Stop();
        var id = Guid.NewGuid().ToString("N");
        s_current = new Session(id, context, program, app);
        return id;
    }

    /// <summary>Stops the live server (if one is running) and frees its program.</summary>
    public static void Stop()
    {
        var session = s_current;
        s_current = null;
        if (session is null) return;
        try { session.App.Dispose(); } catch { /* the learner's Dispose code mustn't block the next run */ }
        session.Context.Dispose();
    }

    public static async Task<LiveResponse> SendAsync(LiveRequest request)
    {
        var session = s_current;
        if (session is null || session.Id != request.Session) return new LiveResponse(false, null, []);

        // What the endpoint prints goes to the app's server log, like `dotnet run` printing in a terminal.
        var console = new List<ConsoleLine>();
        var capture = new ConsoleCapture(console);
        var (oldOut, oldErr) = (Console.Out, Console.Error);
        Console.SetOut(capture.Out);
        Console.SetError(capture.Error);
        try
        {
            var (exchange, problem) = await WebServer.SendAsync(session.App, session.Program, request.Request);
            capture.Flush();
            if (problem is not null) console.Add(new("error", problem));
            return new LiveResponse(true, exchange, console);
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
    }
}
