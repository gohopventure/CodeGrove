using System.Text.Json;
using Codegrove.CSharpRunner;

namespace Codegrove.CSharpRunner.Tests;

/// <summary>
/// Entity Framework Core (in-memory provider) through the runner, for Web API lesson 6. EF Core lives in the
/// shared load context, so these mostly check that nothing leaks between runs or between check servers.
/// </summary>
public class EfTests
{
    private const string Console_ = """
        using Microsoft.EntityFrameworkCore;

        var options = new DbContextOptionsBuilder<ShopDb>().UseInMemoryDatabase("shop").Options;
        using (var db = new ShopDb(options))
        {
            Console.WriteLine($"Before: {db.Items.Count()}");
            db.Items.Add(new Item { Name = "Lantern" });
            db.SaveChanges();
        }
        using (var db = new ShopDb(options))
        {
            var item = db.Items.Single();
            Console.WriteLine($"After: {item.Id} {item.Name}");
        }

        public class Item { public int Id { get; set; } public string Name { get; set; } = ""; }
        public class ShopDb(DbContextOptions<ShopDb> o) : DbContext(o) { public DbSet<Item> Items => Set<Item>(); }
        """;

    private const string Server = """
        using Microsoft.EntityFrameworkCore;

        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddDbContext<GroveDb>(o => o.UseInMemoryDatabase("grove"));
        var app = builder.Build();
        app.MapGet("/treasures", (GroveDb db) => db.Treasures.ToList());
        app.MapPost("/treasures", (Treasure t, GroveDb db) =>
        {
            db.Treasures.Add(t);
            db.SaveChanges();
            return Results.Created($"/treasures/{t.Id}", t);
        });
        app.Run();

        public class Treasure { public int Id { get; set; } public string Name { get; set; } = ""; }
        public class GroveDb(DbContextOptions<GroveDb> o) : DbContext(o) { public DbSet<Treasure> Treasures => Set<Treasure>(); }
        """;

    [Fact]
    public void The_reset_can_find_EF_Cores_cache()
    {
        // If this fails after an EF Core upgrade, EF renamed ServiceProviderCache or its field: update EfReset.
        Assert.True(EfReset.IsSupported);
    }

    [Fact]
    public async Task Each_run_starts_with_an_empty_in_memory_database()
    {
        var files = new Dictionary<string, string> { ["Program.cs"] = Console_ };
        foreach (var _ in Enumerable.Range(0, 2))
        {
            // Run 2 recompiles Item into a new load context. Without the reset it would see run 1's row,
            // typed by run 1's Item class, and fail with "[A]Item cannot be cast to [B]Item".
            var r = await CSharpCodeRunner.RunAsync(new RunRequest(files));
            Assert.Equal(["Before: 0", "After: 1 Lantern"], r.Console.Select(l => l.Text));
        }
    }

    [Fact]
    public async Task Each_check_gets_a_fresh_database()
    {
        var add = new { method = "POST", path = "/treasures", body = new { name = "Golden acorn" } };
        TestSpec Check(int id, object spec) =>
            new(id, $"check {id}", "httpRequest", JsonSerializer.SerializeToElement(spec), $"fail {id}.");

        var r = await CSharpCodeRunner.RunAsync(new RunRequest(
            new() { ["Program.cs"] = Server },
            [
                Check(1, new { before = new[] { add, add }, path = "/treasures", expectJson = new[] { new { id = 1 }, new { id = 2 } } }),
                // Check 1 saved two rows; this one must not see them.
                Check(2, new { path = "/treasures", expectJson = Array.Empty<object>() }),
                Check(3, new { method = "POST", path = "/treasures", body = new { name = "Spiral shell" }, expectStatus = 201, expectJson = new { id = 1 } }),
            ]));

        Assert.All(r.Results, t => Assert.True(t.Passed, t.Message));
    }

    [Fact]
    public async Task Try_it_requests_share_one_database()
    {
        var r = await CSharpCodeRunner.RunAsync(new RunRequest(
            new() { ["Program.cs"] = Server }, null, null,
            [new("POST", "/treasures", """{"name":"Golden acorn"}"""), new("GET", "/treasures")]));

        Assert.Equal(201, r.Responses![0].Status);
        Assert.Contains("Golden acorn", r.Responses[1].Body);
    }

    [Fact]
    public async Task A_DbContext_in_a_singleton_fails_at_Build()
    {
        const string program = """
            using Microsoft.EntityFrameworkCore;
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddDbContext<GroveDb>(o => o.UseInMemoryDatabase("grove"));
            builder.Services.AddSingleton<Leaderboard>();
            var app = builder.Build();
            app.Run();
            public class GroveDb(DbContextOptions<GroveDb> o) : DbContext(o) { }
            public class Leaderboard(GroveDb db) { }
            """;
        var r = await CSharpCodeRunner.RunAsync(new RunRequest(new() { ["Program.cs"] = program }));

        var error = Assert.Single(r.Console, l => l.Level == "error").Text;
        Assert.Contains("Cannot consume scoped service 'GroveDb' from singleton 'Leaderboard'.", error);
    }
}
