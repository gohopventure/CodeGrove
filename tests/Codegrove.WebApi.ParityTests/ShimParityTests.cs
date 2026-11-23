using Codegrove.CSharpRunner;

namespace Codegrove.WebApi.ParityTests;

/// <summary>
/// The same program and requests on the practice server (through the runner) and on real ASP.NET Core must
/// get the same answers. These cover what the lessons and WebApiTests rely on: text vs JSON, routing and
/// constraints, 405, binding rules and their 400 messages, nullability, returning null, the endpoint-building
/// errors for unregistered services, DI validation, results and problem details.
/// </summary>
public class ShimParityTests
{
    private static HttpRequestSpec Get(string path) => new("GET", path);

    public static TheoryData<string> Probes()
    {
        var data = new TheoryData<string>();
        foreach (var name in Programs.Keys) data.Add(name);
        return data;
    }

    private static readonly Dictionary<string, (string Code, HttpRequestSpec[] Requests)> Programs = new()
    {
        ["text, JSON, routes and status codes"] = ("""
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
            app.MapGet("/raw/{id}", (int id) => pets.FirstOrDefault(p => p.Id == id));
            app.Run();
            record Pet(int Id, string Name, string Kind);
            """,
            [Get("/hello"), Get("/pets"), Get("/pets/2"), Get("/pets/9"), Get("/pets/abc"), Get("/nowhere"), Get("/HELLO/"),
             new("DELETE", "/pets"), new("POST", "/pets", """{"name":"Fern","kind":"snail"}"""), Get("/pets/3"),
             Get("/raw/9")]),

        ["query strings, defaults and binding errors"] = ("""
            var app = WebApplication.CreateBuilder(args).Build();
            app.MapGet("/search", (string kind, int? limit) => $"{kind ?? "any"}:{limit ?? 10}");
            app.MapGet("/page", (int number) => $"Page {number}");
            app.MapGet("/books", (string search, int page = 1) => $"search={search ?? "none"} page={page}");
            app.MapPost("/pets", (Pet pet) => pet.Name);
            app.Run();
            record Pet(string Name);
            """,
            [Get("/search"), Get("/search?kind=owl&limit=3"), Get("/search?limit=x"), Get("/page"), Get("/page?number=2"),
             Get("/books"), Get("/books?page=3&search=x"), new("POST", "/pets"), new("POST", "/pets", "{not json"),
             new("POST", "/pets", """{"name":"Moss"}""")]),

        ["nullable enabled"] = ("""
            #nullable enable
            var app = WebApplication.CreateBuilder(args).Build();
            app.MapGet("/a", (string name) => name);
            app.MapGet("/b", (string? name) => name ?? "nobody");
            app.Run();
            """,
            [Get("/a"), Get("/a?name=Ada"), Get("/b")]),

        ["unregistered services"] = ("""
            var app = WebApplication.CreateBuilder(args).Build();
            app.MapGet("/pets", (PetBook book) => book.Count);
            app.MapPost("/pets", (Pet pet, PetBook book) => "added");
            app.Run();
            class PetBook { public int Count => 3; }
            record Pet(string Name);
            """,
            [Get("/pets"), new("POST", "/pets", """{"name":"Moss"}""")]),

        ["one broken endpoint stops every request"] = ("""
            var app = WebApplication.CreateBuilder(args).Build();
            app.MapGet("/ok", () => "fine");
            app.MapGet("/pets", (PetBook book) => 1);
            app.Run();
            class PetBook { }
            """,
            [Get("/nowhere"), Get("/ok"), Get("/pets")]),

        ["two body parameters"] = ("""
            var app = WebApplication.CreateBuilder(args).Build();
            app.MapPost("/pets", (Pet pet, PetBook book) => "added");
            app.Run();
            class PetBook { }
            record Pet(string Name);
            """,
            [new("POST", "/pets", """{"name":"Moss"}""")]),

        ["required and optional bodies"] = ("""
            #nullable enable
            var app = WebApplication.CreateBuilder(args).Build();
            app.MapPost("/req", (Pet pet) => pet.Name);
            app.MapPost("/opt", (Pet? pet) => pet?.Name ?? "nobody");
            app.MapGet("/nothing", () => (string?)null);
            app.Run();
            record Pet(string Name);
            """,
            [new("POST", "/req"), new("POST", "/req", "{}"), new("POST", "/opt"), new("POST", "/opt", """{"name":"Moss"}"""), Get("/nothing")]),

        ["controllers with [ApiController]"] = ("""
            using Microsoft.AspNetCore.Mvc;

            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddControllers();
            builder.Services.AddSingleton<Shelf>();
            var app = builder.Build();
            app.MapControllers();
            app.Run();

            public class Shelf
            {
                public List<Book> Books { get; } = [new(1, "Hatchet", 1987), new(2, "Holes", 1998)];
            }

            public record Book(int Id, string Title, int Year);
            public record NewBook(string Title, int Year);

            [ApiController]
            [Route("api/[controller]")]
            public class BooksController(Shelf shelf) : ControllerBase
            {
                [HttpGet]
                public IEnumerable<Book> GetAll() => shelf.Books;

                [HttpGet("{id:int}")]
                public ActionResult<Book> GetById(int id) =>
                    shelf.Books.FirstOrDefault(b => b.Id == id) is { } book ? book : NotFound();

                [HttpGet("raw/{id}")]
                public Book Raw(int id) => shelf.Books.FirstOrDefault(b => b.Id == id);

                [HttpGet("text")]
                public string Text() => "plain";

                [HttpGet("search")]
                public IEnumerable<Book> Search(string title, int year = 0) =>
                    shelf.Books.Where(b => (title == null || b.Title.Contains(title)) && (year == 0 || b.Year == year));

                [HttpPost]
                public ActionResult<Book> Create(NewBook input)
                {
                    var book = new Book(shelf.Books.Count + 1, input.Title, input.Year);
                    shelf.Books.Add(book);
                    return CreatedAtAction(nameof(GetById), new { id = book.Id }, book);
                }

                [HttpDelete("{id:int}")]
                public IActionResult Delete(int id) =>
                    shelf.Books.RemoveAll(b => b.Id == id) > 0 ? NoContent() : NotFound();

                [HttpGet("closed")]
                public IActionResult Closed() => Problem("Shelf closed.", statusCode: 503);

                [HttpGet("bad")]
                public IActionResult Bad() => BadRequest("Nope.");

                [HttpGet("{id:int}/title")]
                public IActionResult TitleOf(int id) => Ok(shelf.Books.First(b => b.Id == id).Title);
            }
            """,
            [Get("/api/books"), Get("/api/books/1"), Get("/api/books/9"), Get("/api/books/abc"), Get("/API/Books/2/"),
             Get("/api/books/raw/9"), Get("/api/books/raw/abc"), Get("/api/books/text"),
             Get("/api/books/search?title=Hol"), Get("/api/books/search?year=x"), Get("/api/books/search"),
             new("POST", "/api/books", """{"title":"Wonder","year":2012}"""), Get("/api/books/3"),
             new("POST", "/api/books", "{}"), new("POST", "/api/books"), new("POST", "/api/books", "{bad"),
             new("DELETE", "/api/books/1"), new("DELETE", "/api/books/9"), new("PUT", "/api/books"),
             Get("/api/books/closed"), Get("/api/books/bad"), Get("/api/books/2/title"), Get("/api/books/7/title")]),

        ["controller validation with nullable enabled"] = ("""
            #nullable enable
            using System.ComponentModel.DataAnnotations;
            using Microsoft.AspNetCore.Mvc;

            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddControllers();
            var app = builder.Build();
            app.MapControllers();
            app.Run();

            public record NewBook([Required, StringLength(20)] string Title, [Range(1450, 2100)] int Year, string? Note);

            [ApiController]
            [Route("books")]
            public class BooksController : ControllerBase
            {
                [HttpPost]
                public IActionResult Create(NewBook input) => Ok(input);

                [HttpGet]
                public string Find(string title) => title;

                [HttpGet("optional")]
                public string Optional(string? title) => title ?? "none";
            }
            """,
            [new("POST", "/books", """{"title":"Holes","year":1998}"""), new("POST", "/books", """{"year":1998}"""),
             new("POST", "/books", """{"title":"A very long title indeed","year":3000}"""), new("POST", "/books", """{"title":"","year":1998}"""),
             Get("/books"), Get("/books?title=Holes"), Get("/books/optional")]),

        ["controllers without [ApiController]"] = ("""
            using System.ComponentModel.DataAnnotations;
            using Microsoft.AspNetCore.Mvc;

            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddControllers();
            var app = builder.Build();
            app.MapControllers();
            app.Run();

            public record NewBook([Required] string Title, int Year);

            [Route("books")]
            public class BooksController : ControllerBase
            {
                [HttpPost]
                public IActionResult Create(NewBook input) => Ok(input);

                [HttpPost("checked")]
                public IActionResult Checked([FromBody] NewBook input) =>
                    ModelState.IsValid ? Ok(input) : ValidationProblem(ModelState);

                [HttpGet("{id:int}")]
                public IActionResult Get(int id) => id == 1 ? Ok(new { id }) : NotFound();
            }

            class HiddenController : ControllerBase
            {
                [HttpGet("/hidden")]
                public string Get() => "found";
            }
            """,
            [new("POST", "/books", """{"title":"Holes","year":1998}"""), new("POST", "/books?title=Holes&year=1998"),
             new("POST", "/books/checked", """{"year":1998}"""), new("POST", "/books/checked", """{"title":"Holes","year":1998}"""),
             Get("/books/1"), Get("/books/2"), Get("/hidden")]),

        ["controllers with a missing service"] = ("""
            using Microsoft.AspNetCore.Mvc;

            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddControllers();
            var app = builder.Build();
            app.MapGet("/ok", () => "fine");
            app.MapControllers();
            app.Run();

            public class Shelf { }

            [ApiController]
            [Route("books")]
            public class BooksController(Shelf shelf) : ControllerBase
            {
                [HttpGet]
                public string Get() => "books";
            }
            """,
            [Get("/books"), Get("/ok")]),

        ["services and scopes"] = ("""
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddSingleton<ICounter, Counter>();
            builder.Services.AddScoped<RequestId>();
            var app = builder.Build();
            app.MapGet("/count", (ICounter c, RequestId a, RequestId b) => new { count = c.Next(), same = a == b });
            app.Run();
            interface ICounter { int Next(); }
            class Counter : ICounter { int n; public int Next() => ++n; }
            class RequestId { }
            """,
            [Get("/count"), Get("/count")]),

        ["groups, typed results and problem details"] = ("""
            using Microsoft.AspNetCore.Http.HttpResults;

            var app = WebApplication.CreateBuilder(args).Build();
            var books = app.MapGroup("/api/books");
            books.MapGet("/{id:int}", Results<Ok<string>, NotFound> (int id) =>
                id == 1 ? TypedResults.Ok("Hatchet") : TypedResults.NotFound());
            books.MapPost("/", (Book b) => string.IsNullOrEmpty(b.Title)
                ? Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = ["A title is required."] })
                : Results.Created($"/api/books/{b.Id}", b));
            books.MapDelete("/{id:int}", (int id) => id == 1 ? Results.NoContent() : Results.Problem("Only book 1 can go.", statusCode: 409));
            app.MapGet("/down", () => Results.Problem("Back soon.", statusCode: 503));
            app.MapGet("/text", () => Results.Text("plain"));
            app.Run();
            record Book(int Id, string Title);
            """,
            [Get("/api/books/1"), Get("/api/books/2"), new("POST", "/api/books", """{"id":5,"title":""}"""),
             new("POST", "/api/books", """{"id":5,"title":"Holes"}"""), new("DELETE", "/api/books/1"), new("DELETE", "/api/books/7"),
             Get("/down"), Get("/text")]),

        ["async handlers"] = ("""
            var app = WebApplication.CreateBuilder(args).Build();
            app.MapGet("/slow", async () => { await Task.Yield(); return new { done = true }; });
            app.MapGet("/nothing", async () => { await Task.Yield(); });
            app.MapGet("/void", () => { });
            await app.RunAsync();
            """,
            [Get("/slow"), Get("/nothing"), Get("/void")]),

        ["a crashing endpoint"] = ("""
            var app = WebApplication.CreateBuilder(args).Build();
            string[] names = ["Moss"];
            app.MapGet("/name/{i:int}", (int i) => names[i]);
            app.Run();
            """,
            [Get("/name/0"), Get("/name/5")]),
    };

    [Theory]
    [MemberData(nameof(Probes))]
    public async Task The_practice_server_answers_like_ASP_NET_Core(string probe)
    {
        var (code, requests) = Programs[probe];
        var files = new Dictionary<string, string> { ["Program.cs"] = code };

        var shim = await CSharpCodeRunner.RunAsync(new RunRequest(files, null, null, [.. requests]));
        Assert.True(shim.Compiled, string.Join("\n", shim.Console.Select(l => l.Text)));
        Assert.NotNull(shim.Responses);

        var program = RealServer.Compile(files);
        var real = new List<HttpExchange>();
        await using (var app = await RealServer.StartAsync(program))
        {
            foreach (var r in requests) real.Add(await app.SendAsync(r));
        }

        var differences = shim.Responses!.Zip(real, Parity.Diff).OfType<string>().ToList();
        Assert.True(differences.Count == 0, $"{probe}:\n" + string.Join("\n", differences));
    }

    [Theory]
    [InlineData("""
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton<Report>();
        builder.Services.AddScoped<Clock>();
        var app = builder.Build();
        app.Run();
        class Clock { }
        class Report(Clock clock) { }
        """)]
    [InlineData("""
        var builder = WebApplication.CreateBuilder(args);
        var app = builder.Build();
        app.MapControllers();
        app.Run();
        """)]
    [InlineData("""
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddScoped<Report>();
        var app = builder.Build();
        app.Run();
        interface IClock { }
        class Report(IClock clock) { }
        """)]
    public async Task Startup_errors_have_the_same_message(string code)
    {
        var files = new Dictionary<string, string> { ["Program.cs"] = code };
        var shim = await CSharpCodeRunner.RunAsync(new RunRequest(files));
        var shimError = Assert.Single(shim.Console, l => l.Level == "error").Text;

        var failed = await Assert.ThrowsAsync<ProgramFailedException>(() => RealServer.StartAsync(RealServer.Compile(files)));
        Assert.Equal(failed.InnerException!.GetType().Name, shimError.Split(' ')[0]);
        Assert.Contains(failed.InnerException.Message, shimError);
    }
}
