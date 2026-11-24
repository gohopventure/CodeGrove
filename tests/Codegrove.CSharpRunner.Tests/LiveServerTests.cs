using System.Text.Json;
using Codegrove.CSharpRunner;

namespace Codegrove.CSharpRunner.Tests;

/// <summary>
/// The capstone's live API: a run with KeepServer leaves the learner's server running, and the React preview
/// sends it requests one at a time. Its in-memory database keeps rows between requests until the next run.
/// </summary>
public class LiveServerTests
{
    private const string Clubs = """
        using Microsoft.EntityFrameworkCore;

        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddDbContext<GroveDb>(o => o.UseInMemoryDatabase("grove"));
        var app = builder.Build();

        app.MapGet("/api/clubs", (GroveDb db) => db.Clubs.OrderBy(c => c.Id).ToList());
        app.MapPost("/api/clubs", (Club club, GroveDb db) =>
        {
            Console.WriteLine($"New club: {club.Name}");
            db.Clubs.Add(club);
            db.SaveChanges();
            return Results.Created($"/api/clubs/{club.Id}", club);
        });
        app.MapGet("/api/crash", () =>
        {
            string name = null;
            return name.Length;
        });
        app.MapGet("/api/forever", () =>
        {
            var n = 0;
            while (true) n++;
        });
        app.Run();

        public class Club { public int Id { get; set; } public string Name { get; set; } = ""; }
        public class GroveDb(DbContextOptions<GroveDb> o) : DbContext(o) { public DbSet<Club> Clubs => Set<Club>(); }
        """;

    private static Task<RunResult> Start(string program, params TestSpec[] tests) =>
        CSharpCodeRunner.RunAsync(new RunRequest(new() { ["api/Program.cs"] = program }, [.. tests], KeepServer: true));

    private static Task<LiveResponse> Send(string session, string method, string path, string? body = null) =>
        CSharpCodeRunner.SendToLiveServerAsync(new LiveRequest(session, new HttpRequestSpec(method, path, body)));

    [Fact]
    public async Task Requests_share_one_database_while_the_server_runs()
    {
        var run = await Start(Clubs);
        Assert.NotNull(run.LiveSession);

        var created = await Send(run.LiveSession!, "POST", "/api/clubs", """{"name":"Owl Watchers"}""");
        Assert.True(created.Found);
        Assert.Equal(201, created.Exchange!.Status);
        Assert.Equal([new ConsoleLine("log", "New club: Owl Watchers")], created.Console);

        await Send(run.LiveSession!, "POST", "/api/clubs", """{"name":"Frog Choir"}""");
        var list = await Send(run.LiveSession!, "GET", "/api/clubs");
        Assert.Equal("""[{"id":1,"name":"Owl Watchers"},{"id":2,"name":"Frog Choir"}]""", list.Exchange!.Body);
    }

    [Fact]
    public async Task The_next_run_stops_the_server_and_starts_with_an_empty_database()
    {
        var first = await Start(Clubs);
        await Send(first.LiveSession!, "POST", "/api/clubs", """{"name":"Owl Watchers"}""");

        var second = await Start(Clubs);
        Assert.NotEqual(first.LiveSession, second.LiveSession);
        Assert.False((await Send(first.LiveSession!, "GET", "/api/clubs")).Found);
        Assert.Equal("[]", (await Send(second.LiveSession!, "GET", "/api/clubs")).Exchange!.Body);

        // A plain run (no KeepServer) stops it too: it resets EF Core's databases.
        await CSharpCodeRunner.RunAsync(new RunRequest(new() { ["Program.cs"] = "Console.WriteLine(1);" }));
        Assert.False((await Send(second.LiveSession!, "GET", "/api/clubs")).Found);
    }

    [Fact]
    public async Task After_http_checks_the_live_server_starts_fresh()
    {
        // Each httpRequest check starts and resets its own server; the live one mustn't see their rows.
        var check = new TestSpec(1, "post", "httpRequest", JsonSerializer.SerializeToElement(new
        {
            method = "POST", path = "/api/clubs", body = """{"name":"Check club"}""", expectStatus = 201,
        }), "fail.");
        var run = await Start(Clubs, check);
        Assert.True(Assert.Single(run.Results).Passed);
        Assert.Equal("[]", (await Send(run.LiveSession!, "GET", "/api/clubs")).Exchange!.Body);
    }

    [Fact]
    public async Task A_crash_is_a_500_with_the_reason_in_the_server_log_and_the_server_keeps_running()
    {
        var run = await Start(Clubs);
        var crash = await Send(run.LiveSession!, "GET", "/api/crash");
        Assert.Equal(500, crash.Exchange!.Status);
        var line = Assert.Single(crash.Console);
        Assert.Equal("error", line.Level);
        Assert.Contains("GET /api/crash crashed", line.Text);
        Assert.Contains("NullReferenceException in api/Program.cs, line 18", line.Text);

        Assert.Equal(200, (await Send(run.LiveSession!, "GET", "/api/clubs")).Exchange!.Status);
    }

    [Fact]
    public async Task An_endless_loop_is_stopped_and_the_next_request_gets_a_fresh_step_budget()
    {
        var run = await Start(Clubs);
        var stuck = await Send(run.LiveSession!, "GET", "/api/forever");
        Assert.Equal(500, stuck.Exchange!.Status);
        Assert.Contains(stuck.Console, l => l.Text.Contains("ran too many loop steps"));
        Assert.Equal(200, (await Send(run.LiveSession!, "GET", "/api/clubs")).Exchange!.Status);
    }

    [Fact]
    public async Task No_session_when_the_program_never_starts_a_server()
    {
        var noRun = await Start("var app = WebApplication.CreateBuilder(args).Build();\napp.MapGet(\"/\", () => 1);");
        Assert.Null(noRun.LiveSession);

        var broken = await Start("var app = WebApplication.CreateBuilder(args).Build()\napp.Run();");
        Assert.False(broken.Compiled);
        Assert.Null(broken.LiveSession);

        var plain = await CSharpCodeRunner.RunAsync(new RunRequest(new() { ["Program.cs"] = Clubs }));
        Assert.Null(plain.LiveSession); // KeepServer wasn't asked for
    }
}
