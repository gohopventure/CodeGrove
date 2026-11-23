using System.Text.Json;
using Codegrove.CSharpRunner;

namespace Codegrove.WebApi.ParityTests;

/// <summary>
/// Every published lesson solution that starts a web server must pass its own httpRequest checks on real
/// ASP.NET Core too, with each check on a freshly started server, as the runner does. If one fails here but
/// passes in the runner, the practice server has drifted from ASP.NET Core (or the lesson relies on
/// something only the practice server does).
/// </summary>
public class LessonParityTests
{
    private static readonly string CoursesDir = Path.Combine(RepoRoot(), "src", "Codegrove.Api", "Content", "courses");

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Codegrove.sln"))) return dir.FullName;
        throw new InvalidOperationException("Couldn't find the repository root (Codegrove.sln).");
    }

    public static TheoryData<string, string> WebLessons()
    {
        var data = new TheoryData<string, string>();
        foreach (var path in Directory.GetFiles(CoursesDir, "*.json").Order())
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            foreach (var lesson in doc.RootElement.GetProperty("lessons").EnumerateArray())
            {
                if (!lesson.TryGetProperty("isPublished", out var p) || !p.GetBoolean()) continue;
                if (!lesson.TryGetProperty("challenge", out var c) || c.ValueKind != JsonValueKind.Object) continue;
                if (c.GetProperty("kind").GetString() != "CSharp") continue;
                if (!c.GetProperty("tests").EnumerateArray().Any(t => t.GetProperty("kind").GetString() == WebChecks.Kind)) continue;
                data.Add(Path.GetFileName(path), lesson.GetProperty("slug").GetString()!);
            }
        }
        return data;
    }

    [Fact]
    public void There_are_web_lessons_to_check() => Assert.NotEmpty(WebLessons());

    [Theory]
    [MemberData(nameof(WebLessons))]
    public async Task Solution_passes_its_web_checks_on_real_ASP_NET_Core(string file, string slug)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(CoursesDir, file)));
        var challenge = doc.RootElement.GetProperty("lessons").EnumerateArray()
            .Single(l => l.GetProperty("slug").GetString() == slug).GetProperty("challenge");
        var files = challenge.GetProperty("solutionFiles").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        var program = RealServer.Compile(files);

        var failures = new List<string>();
        foreach (var test in challenge.GetProperty("tests").EnumerateArray().Where(t => t.GetProperty("kind").GetString() == WebChecks.Kind))
        {
            var label = test.GetProperty("label").GetString();
            var spec = test.GetProperty("spec");
            await using var app = await RealServer.StartAsync(program);
            if (spec.TryGetProperty("before", out var before))
                foreach (var step in before.EnumerateArray())
                    await app.SendAsync(WebChecks.RequestFrom(step));
            var got = await app.SendAsync(WebChecks.RequestFrom(spec));
            if (WebChecks.Mismatch(spec, got) is { } part)
                failures.Add($"{label}: {part} differs. Real ASP.NET Core answered {WebChecks.Describe(got)}");
        }
        Assert.True(failures.Count == 0, $"{slug}:\n" + string.Join("\n", failures));
    }
}
