using System.Reflection;
using Codegrove.WebShim;
using Microsoft.AspNetCore.Builder;
using static Codegrove.CSharpRunner.CSharpCodeRunner;

namespace Codegrove.CSharpRunner;

/// <summary>
/// The runner's side of the Web API course: after a program has run, report on the server it started (if
/// any), send it the Try it panel's requests, and give checks a freshly started copy of it.
/// </summary>
internal static class WebServer
{
    /// <summary>Server info and the answers to <paramref name="requests"/>; both null for a program that built no web app.</summary>
    public static async Task<(ServerInfo? Server, List<HttpExchange>? Responses)> AfterRunAsync(
        ShimHost.Slot slot, RunOutcome outcome, Assembly program, List<HttpRequestSpec>? requests, List<ConsoleLine> console)
    {
        if (slot.Built is null) return (null, null);
        var running = slot.Running;
        var server = new ServerInfo(running is not null, slot.Built.Core.Url, Endpoints(slot.Built));

        if (outcome.Stopped == StopReason.None)
        {
            if (running is null)
                console.Add(new("info", "Your app was built but never started, so it can't answer requests. Add app.Run(); as the last line of Program.cs."));
            else if (server.Endpoints.Count == 0)
                console.Add(new("info", $"Your server is running at {server.Url}, but it has no endpoints yet, so every request gets 404 Not Found. Add one with app.MapGet, or map your controllers with app.MapControllers()."));
            else
                console.Add(new("info", $"Your server is running at {server.Url}. Its endpoints: {string.Join(", ", server.Endpoints)}."));
        }

        if (running is null || requests is not { Count: > 0 }) return (server, running is null ? null : []);
        var responses = new List<HttpExchange>();
        foreach (var r in requests)
        {
            var (exchange, problem) = await SendAsync(running, program, r);
            responses.Add(exchange);
            if (problem is not null) console.Add(new("error", problem));
        }
        return (server, responses);
    }

    public static List<string> Endpoints(WebApplication app) =>
        app.Core.Endpoints.Select(e => $"{(e.Methods.Length == 0 ? "ANY" : string.Join("/", e.Methods))} {e.Pattern.Text}").ToList();

    /// <summary>
    /// Sends one request. A crash in the learner's endpoint comes back as a 500 plus a plain-words problem
    /// ("GET /pets/9 crashed: NullReferenceException in Program.cs, line 8: …") for the Output panel.
    /// </summary>
    public static async Task<(HttpExchange Exchange, string? Problem)> SendAsync(WebApplication app, Assembly program, HttpRequestSpec r)
    {
        // Each request gets its own step budget, and runtime errors are blamed on this request's lines only.
        ResetGuard(program);
        var path = string.IsNullOrWhiteSpace(r.Path) ? "/" : r.Path.Trim();
        if (!path.StartsWith('/')) path = "/" + path;
        var response = await ShimHost.SendAsync(app, new ShimRequest(r.Method, path, r.Body, r.Headers));
        var exchange = new HttpExchange(r.Method.ToUpperInvariant(), path, response.Status, response.Reason, response.ContentType, response.Body,
            new Dictionary<string, string>(response.Headers, StringComparer.OrdinalIgnoreCase));

        string? problem = null;
        if (GuardStopped(program) != StopReason.None)
            problem = $"{exchange.Method} {path} was stopped: your endpoint ran too many loop steps or method calls. Is there a loop that never ends?";
        else if (response.Error is RequestPipeline.EndpointSetupException setup)
            problem = DescribeSetup(exchange.Method, path, setup);
        else if (response.Error is { } error)
            problem = $"{exchange.Method} {path} crashed, so the server answered 500 Internal Server Error. {DescribeException(error, program)}";
        return (exchange, problem);
    }

    /// <summary>
    /// One endpoint ASP.NET Core can't build stops every request (it builds them all together). The usual
    /// cause: a class meant as a service that was never registered, so ASP.NET Core takes it for the body.
    /// </summary>
    private static string DescribeSetup(string method, string path, RequestPipeline.EndpointSetupException setup)
    {
        var unplaced = string.Join(" and ", setup.Unplaced.Distinct());
        var endpointMethod = setup.EndpointText.Split(' ')[0].Split('/')[0];
        var why = setup.NoBodyMethod
            ? $"It asks for {Article(unplaced)}, and ASP.NET Core thinks that should come from the request body, but a {endpointMethod} request has no body."
            : $"It asks for {string.Join(" and ", setup.BodyTypes.Concat(setup.Unplaced).Distinct())}, and a request has only one body, so ASP.NET Core can't tell where {unplaced} should come from.";
        return $"{method} {path}{CantRun} Every request fails until the endpoint {setup.EndpointText} is fixed. {why} " +
            $"If {unplaced} is a service, register it before builder.Build(), like builder.Services.AddSingleton<{setup.Unplaced[^1]}>(); ({setup.Message.Split(Environment.NewLine)[0]})";
    }

    private static string Article(string name) => ("aeiouAEIOU".Contains(name[0]) ? "an " : "a ") + name;

    internal const string CantRun = " got 500 Internal Server Error, because one endpoint can't be built.";

    /// <summary>The plain-words middle of a <see cref="DescribeSetup"/> message, for a check's failure text.</summary>
    internal static string SetupReason(string problem)
    {
        var start = problem.IndexOf(CantRun, StringComparison.Ordinal) + CantRun.Length;
        var end = problem.LastIndexOf(" (", StringComparison.Ordinal);
        return problem[start..(end > start ? end : problem.Length)].Trim();
    }

    /// <summary>
    /// Runs the program again from the top with its output thrown away and returns the app it started, so
    /// every check gets a server in its starting state. Null when the program never reached app.Run().
    /// </summary>
    public static async Task<(WebApplication? App, string? Why)> StartFreshAsync(Assembly program)
    {
        ResetGuard(program);
        EfReset.Reset(); // each check starts with an empty in-memory database
        var slot = ShimHost.Begin();
        RunOutcome outcome;
        try
        {
            outcome = await RunProgramAsync(program, "", []);
        }
        finally
        {
            ShimHost.End();
        }
        if (outcome.Stopped != StopReason.None)
            return (null, "(Your program stopped with an error before the server started. The Output panel says where.)");
        if (slot.Built is null)
            return (null, "(Your program doesn't build a web app yet: it needs WebApplication.CreateBuilder and builder.Build().)");
        if (slot.Running is null)
            return (null, "(Your server never started: add app.Run(); as the last line.)");
        return (slot.Running, null);
    }
}
