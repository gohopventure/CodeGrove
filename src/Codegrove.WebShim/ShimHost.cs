using Microsoft.AspNetCore.Builder;

namespace Codegrove.WebShim;

/// <summary>A request for the practice server (from the Try it panel or a check).</summary>
internal sealed record ShimRequest(string Method, string Path, string? Body = null, IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>
/// What the server answered. <paramref name="Error"/> is the learner's exception when an endpoint crashed
/// (Status is then 500), so the runner can say where it happened in their code.
/// </summary>
internal sealed record ShimResponse(int Status, string? ContentType, string Body, IReadOnlyDictionary<string, string> Headers, Exception? Error)
{
    public string Reason => ShimJson.Reason(Status);
}

/// <summary>
/// Where a running program's app is found. The runner gives each run its own slot (an AsyncLocal, so runs
/// in parallel test threads don't see each other's apps) and reads it after the program has finished.
/// </summary>
internal static class ShimHost
{
    internal sealed class Slot
    {
        public WebApplication? Built { get; set; }
        public WebApplication? Running { get; set; }
    }

    private static readonly AsyncLocal<Slot?> s_current = new();

    /// <summary>Call before running a program; returns the slot its app will land in.</summary>
    public static Slot Begin()
    {
        var slot = new Slot();
        s_current.Value = slot;
        return slot;
    }

    public static void End() => s_current.Value = null;

    internal static void Built(WebApplication app)
    {
        if (s_current.Value is { } slot) slot.Built = app;
    }

    internal static void Started(WebApplication app)
    {
        if (s_current.Value is { } slot) slot.Running = app;
    }

    /// <summary>Sends one request to a running app.</summary>
    public static Task<ShimResponse> SendAsync(WebApplication app, ShimRequest request) => RequestPipeline.HandleAsync(app.Core, request);
}
