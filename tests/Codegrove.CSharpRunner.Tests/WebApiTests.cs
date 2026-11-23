using System.Text.Json;
using Codegrove.CSharpRunner;

namespace Codegrove.CSharpRunner.Tests;

/// <summary>
/// The practice web server (src/Codegrove.WebShim) through the runner: what a learner's ASP.NET Core
/// program answers. Expected values are what real ASP.NET Core answers for the same code.
/// </summary>
public class WebApiTests
{
    private static Task<RunResult> Run(string program, List<HttpRequestSpec>? requests = null, params TestSpec[] tests) =>
        CSharpCodeRunner.RunAsync(new RunRequest(new() { ["Program.cs"] = program }, [.. tests], null, requests));

    private static TestSpec Http(int id, object spec) =>
        new(id, $"test {id}", "httpRequest", JsonSerializer.SerializeToElement(spec), $"fail {id}.");

    private static HttpRequestSpec Get(string path) => new("GET", path);

    private const string Pets = """
        var builder = WebApplication.CreateBuilder(args);
        var app = builder.Build();

        var pets = new List<Pet> { new(1, "Moss", "frog"), new(2, "Pip", "owl") };

        app.MapGet("/hello", () => "Hello from the Grove!");
        app.MapGet("/pets", () => pets);
        app.MapGet("/pets/{id:int}", (int id) =>
            pets.FirstOrDefault(p => p.Id == id) is { } pet ? Results.Ok(pet) : Results.NotFound());
        app.MapPost("/pets", (Pet pet) =>
        {
            var added = pet with { Id = pets.Count + 1 };
            pets.Add(added);
            return Results.Created($"/pets/{added.Id}", added);
        });
        app.MapGet("/search", (string kind, int? limit) => pets.Where(p => p.Kind == kind).Take(limit ?? 10));
        app.MapGet("/page", (int number) => $"Page {number}");

        app.Run();
        Console.WriteLine("never printed: app.Run() keeps the server running");

        record Pet(int Id, string Name, string Kind);
        """;

    [Fact]
    public async Task A_string_is_plain_text_and_an_object_is_camelCase_json()
    {
        var r = await Run(Pets, [Get("/hello"), Get("/pets")]);

        Assert.True(r.Compiled);
        Assert.Equal(StopReason.None, r.Stopped);
        Assert.Equal("", r.Output); // nothing after app.Run() ran
        Assert.True(r.Server!.Running);
        Assert.Equal(["GET /hello", "GET /pets", "GET /pets/{id:int}", "POST /pets", "GET /search", "GET /page"], r.Server.Endpoints);
        Assert.Contains(r.Console, l => l.Level == "info" && l.Text.Contains("Your server is running at http://localhost:5000"));

        var hello = r.Responses![0];
        Assert.Equal((200, "OK", "text/plain; charset=utf-8", "Hello from the Grove!"), (hello.Status, hello.Reason, hello.ContentType, hello.Body));
        var list = r.Responses[1];
        Assert.Equal("application/json; charset=utf-8", list.ContentType);
        Assert.Equal("""[{"id":1,"name":"Moss","kind":"frog"},{"id":2,"name":"Pip","kind":"owl"}]""", list.Body);
    }

    [Fact]
    public async Task Routes_match_values_and_constraints_and_unknown_paths_are_404()
    {
        var r = await Run(Pets, [Get("/pets/2"), Get("/pets/9"), Get("/pets/abc"), Get("/nowhere"), Get("/HELLO/")]);
        Assert.Equal([200, 404, 404, 404, 200], r.Responses!.Select(x => x.Status));
        Assert.Equal("""{"id":2,"name":"Pip","kind":"owl"}""", r.Responses![0].Body);
        Assert.Equal("", r.Responses[1].Body); // Results.NotFound() has no body
    }

    [Fact]
    public async Task A_wrong_method_is_405_with_an_Allow_header()
    {
        var r = await Run(Pets, [new("DELETE", "/pets")]);
        var x = Assert.Single(r.Responses!);
        Assert.Equal(405, x.Status);
        Assert.Equal("GET, POST", x.Headers["Allow"]);
    }

    [Fact]
    public async Task Post_reads_the_json_body_and_requests_share_one_server()
    {
        var r = await Run(Pets, [new("POST", "/pets", """{"name":"Fern","kind":"snail"}"""), Get("/pets/3")]);
        var created = r.Responses![0];
        Assert.Equal(201, created.Status);
        Assert.Equal("/pets/3", created.Headers["Location"]);
        Assert.Equal("""{"id":3,"name":"Fern","kind":"snail"}""", created.Body);
        Assert.Equal(200, r.Responses[1].Status);
    }

    [Fact]
    public async Task Binding_problems_are_400s_with_ASP_NET_Cores_messages()
    {
        var r = await Run(Pets, [Get("/page"), Get("/search?kind=owl&limit=x"), new("POST", "/pets"), new("POST", "/pets", "{not json")]);
        Assert.Equal([400, 400, 400, 400], r.Responses!.Select(x => x.Status));
        Assert.Equal("Required parameter \"int number\" was not provided from query string.", r.Responses![0].Body);
        Assert.Equal("Failed to bind parameter \"int? limit\" from \"x\".", r.Responses[1].Body);
        Assert.StartsWith("Implicit body inferred for parameter \"Pet pet\" but no body was provided.", r.Responses[2].Body);
        Assert.Equal("Failed to read parameter \"Pet pet\" from the request body as JSON.", r.Responses[3].Body);

        // With nullable off (the runner's default), a plain string is optional, as in ASP.NET Core.
        var ok = await Run(Pets, [Get("/search?kind=owl"), Get("/search"), Get("/page?number=2")]);
        Assert.Equal("""[{"id":2,"name":"Pip","kind":"owl"}]""", ok.Responses![0].Body);
        Assert.Equal((200, "[]"), (ok.Responses[1].Status, ok.Responses[1].Body));
        Assert.Equal("Page 2", ok.Responses[2].Body);
    }

    [Fact]
    public async Task Nullable_enabled_makes_a_plain_string_required_and_a_string_question_mark_optional()
    {
        var r = await Run("""
            #nullable enable
            var app = WebApplication.CreateBuilder(args).Build();
            app.MapGet("/a", (string name) => name);
            app.MapGet("/b", (string? name) => name ?? "nobody");
            app.Run();
            """, [Get("/a"), Get("/b")]);
        Assert.Equal([400, 200], r.Responses!.Select(x => x.Status));
        Assert.Equal("nobody", r.Responses![1].Body);
    }

    [Fact]
    public async Task A_crashing_endpoint_answers_500_and_says_where_in_plain_words()
    {
        var r = await Run("""
            var app = WebApplication.CreateBuilder(args).Build();
            string[] names = ["Moss"];
            app.MapGet("/name/{i:int}", (int i) =>
            {
                var name = names[i];
                return name;
            });
            app.Run();
            """, [Get("/name/0"), Get("/name/5")]);
        Assert.Equal([200, 500], r.Responses!.Select(x => x.Status));
        var e = Assert.Single(r.Console, l => l.Level == "error");
        Assert.StartsWith("GET /name/5 crashed, so the server answered 500 Internal Server Error. IndexOutOfRangeException in Program.cs, line 5:", e.Text);
    }

    [Fact]
    public async Task An_endless_loop_in_an_endpoint_is_stopped()
    {
        var r = await Run("""
            var app = WebApplication.CreateBuilder(args).Build();
            app.MapGet("/spin", () => { var n = 0; while (true) { n++; } return n; });
            app.MapGet("/ok", () => "fine");
            app.Run();
            """, [Get("/spin"), Get("/ok")]);
        Assert.Equal([500, 200], r.Responses!.Select(x => x.Status));
        Assert.Contains(r.Console, l => l.Level == "error" && l.Text.StartsWith("GET /spin was stopped"));
    }

    [Fact]
    public async Task Forgetting_app_Run_is_explained()
    {
        var r = await Run("""
            var app = WebApplication.CreateBuilder(args).Build();
            app.MapGet("/", () => "hi");
            """, [Get("/")], Http(1, new { path = "/", expectStatus = 200 }));
        Assert.False(r.Server!.Running);
        Assert.Null(r.Responses);
        Assert.Contains(r.Console, l => l.Level == "info" && l.Text.Contains("never started"));
        Assert.Equal("fail 1. (Your server never started: add app.Run(); as the last line.)", r.Results[0].Message);
    }

    [Fact]
    public async Task Services_are_injected_with_scopes_per_request()
    {
        var r = await Run("""
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddSingleton<ICounter, Counter>();
            builder.Services.AddScoped<RequestId>();
            var app = builder.Build();
            app.MapGet("/count", (ICounter c, RequestId a, RequestId b) => new { count = c.Next(), same = a == b });
            app.Run();

            interface ICounter { int Next(); }
            class Counter : ICounter { int n; public int Next() => ++n; }
            class RequestId { }
            """, [Get("/count"), Get("/count")]);
        Assert.Equal("""{"count":1,"same":true}""", r.Responses![0].Body);
        Assert.Equal("""{"count":2,"same":true}""", r.Responses[1].Body);
    }

    [Fact]
    public async Task A_scoped_service_inside_a_singleton_fails_at_Build_like_ASP_NET_Core()
    {
        var r = await Run("""
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddSingleton<Report>();
            builder.Services.AddScoped<Clock>();
            var app = builder.Build();
            app.Run();

            class Clock { }
            class Report(Clock clock) { }
            """);
        Assert.Equal(StopReason.Exception, r.Stopped);
        var e = Assert.Single(r.Console, l => l.Level == "error");
        Assert.Contains("AggregateException in Program.cs, line 4", e.Text);
        Assert.Contains("Cannot consume scoped service 'Clock' from singleton 'Report'.", e.Text);
    }

    [Fact]
    public async Task Groups_typed_results_and_problem_details()
    {
        var r = await Run("""
            using Microsoft.AspNetCore.Http.HttpResults;

            var app = WebApplication.CreateBuilder(args).Build();
            var books = app.MapGroup("/api/books");
            books.MapGet("/{id:int}", Results<Ok<string>, NotFound> (int id) =>
                id == 1 ? TypedResults.Ok("Hatchet") : TypedResults.NotFound());
            books.MapPost("/", (Book b) => string.IsNullOrEmpty(b.Title)
                ? Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = ["A title is required."] })
                : Results.Created($"/api/books/{b.Id}", b));
            books.MapDelete("/{id:int}", (int id) => id == 1 ? Results.NoContent() : Results.Problem("Only book 1 can go.", statusCode: 409));
            app.Run();

            record Book(int Id, string Title);
            """, [Get("/api/books/1"), Get("/api/books/2"), new("POST", "/api/books", """{"id":5,"title":""}"""), new("DELETE", "/api/books/1"), new("DELETE", "/api/books/7")]);
        Assert.True(r.Compiled, string.Join("\n", r.Console.Select(c => c.Text)));
        var x = r.Responses!;
        Assert.Equal([200, 404, 400, 204, 409], x.Select(e => e.Status));
        Assert.Equal("\"Hatchet\"", x[0].Body);
        Assert.Equal("application/problem+json", x[2].ContentType);
        Assert.Equal("""{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"title":["A title is required."]}}""", x[2].Body);
        Assert.Equal("""{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.10","title":"Conflict","status":409,"detail":"Only book 1 can go."}""", x[4].Body);
    }

    [Fact]
    public async Task Async_handlers_and_async_programs_work()
    {
        var r = await Run("""
            var app = WebApplication.CreateBuilder(args).Build();
            app.MapGet("/slow", async () => { await Task.Yield(); return new { done = true }; });
            app.MapGet("/nothing", async () => { await Task.Yield(); });
            await app.RunAsync();
            """, [Get("/slow"), Get("/nothing")]);
        Assert.Equal("""{"done":true}""", r.Responses![0].Body);
        Assert.Equal((200, ""), (r.Responses[1].Status, r.Responses[1].Body));
    }

    [Fact]
    public async Task HttpRequest_checks_get_a_fresh_server_and_say_what_came_back()
    {
        var r = await Run(Pets, null,
            Http(1, new { method = "POST", path = "/pets", body = new { name = "Fern", kind = "snail" }, expectStatus = 201, expectJson = new { id = 3, name = "Fern" }, expectHeader = new { Location = "/pets/3" } }),
            // Fresh server: the POST above didn't happen here.
            Http(2, new { path = "/pets/3", expectStatus = 404 }),
            Http(3, new { before = new[] { new { method = "POST", path = "/pets", body = new { name = "Fern", kind = "snail" } } }, path = "/pets/3", expectStatus = 200, expectJson = new { kind = "snail" } }),
            Http(4, new { path = "/pets", expectJson = new[] { new { name = "Pip" }, new { name = "Moss" } }, anyOrder = true }),
            Http(5, new { path = "/hello", expectBody = "Hello from the Grove!", expectContentType = "text/plain" }),
            Http(6, new { path = "/pets/9", expectStatus = 200 }),
            Http(7, new { path = "/hello", expectJson = new { message = "hi" } }));
        Assert.Equal([true, true, true, true, true, false, false], r.Results.Select(t => t.Passed));
        Assert.Equal("fail 6. (It answered 404 Not Found with no body.)", r.Results[5].Message);
        Assert.Equal("fail 7. (It answered 200 OK with the text Hello from the Grove!)", r.Results[6].Message);
    }

    [Fact]
    public async Task A_check_on_a_crashing_endpoint_names_the_exception()
    {
        var r = await Run("""
            var app = WebApplication.CreateBuilder(args).Build();
            app.MapGet("/boom", () => { string s = null; return s.Length; });
            app.Run();
            """, null, Http(1, new { path = "/boom", expectStatus = 200 }));
        Assert.StartsWith("fail 1. (It answered 500 Internal Server Error with the text System.NullReferenceException:", r.Results[0].Message);
        Assert.EndsWith("Your endpoint stopped with NullReferenceException in Program.cs, line 2.)", r.Results[0].Message);
    }

    [Fact]
    public async Task Common_web_compile_errors_are_explained()
    {
        var mixed = await Run("""
            var app = WebApplication.CreateBuilder(args).Build();
            var closed = true;
            app.MapGet("/play", () => closed ? Results.Problem("Closed.", statusCode: 503) : "Open!");
            app.Run();
            """);
        Assert.Contains("wrap the plain value too", Assert.Single(mixed.Diagnostics, d => d.Severity == "error").Friendly);

        var noUsing = await Run("""
            var app = WebApplication.CreateBuilder(args).Build();
            app.MapGet("/", Results<Ok<string>, NotFound> () => TypedResults.Ok("hi"));
            app.Run();
            """);
        Assert.All(noUsing.Diagnostics.Where(d => d.Severity == "error"),
            d => Assert.Contains("using Microsoft.AspNetCore.Http.HttpResults;", d.Friendly));
    }

    [Fact]
    public async Task Programs_without_a_web_app_are_unchanged()
    {
        var r = await Run("Console.WriteLine(\"plain\");");
        Assert.Null(r.Server);
        Assert.Null(r.Responses);
        Assert.Equal(["plain"], r.Console.Select(l => l.Text));
    }
}
