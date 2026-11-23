using System.Text.Json;

namespace Codegrove.CSharpRunner.Tests;

/// <summary>
/// Guards authored C# content, like web/src/runner/content.test.ts does for the web lessons: for every published
/// C# challenge in the API's seed files, the solution passes all of its tests and the starter fails at least one;
/// every worked example compiles and runs cleanly. Uses the same runner code the browser runs.
/// </summary>
public class ContentTests
{
    private static readonly string CoursesDir = Path.Combine(RepoRoot(), "src", "Codegrove.Api", "Content", "courses");

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Codegrove.sln"))) return dir.FullName;
        throw new InvalidOperationException("Couldn't find the repository root (Codegrove.sln).");
    }

    public static TheoryData<string, string> CSharpLessons()
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
                data.Add(Path.GetFileName(path), lesson.GetProperty("slug").GetString()!);
            }
        }
        return data;
    }

    private static JsonElement Lesson(string file, string slug)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(CoursesDir, file)));
        return doc.RootElement.GetProperty("lessons").EnumerateArray().Single(l => l.GetProperty("slug").GetString() == slug).Clone();
    }

    private static Dictionary<string, string> Files(JsonElement e) =>
        e.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);

    [Fact]
    public void There_are_CSharp_lessons_to_check() => Assert.NotEmpty(CSharpLessons());

    [Theory]
    [MemberData(nameof(CSharpLessons))]
    public async Task Solution_passes_and_starter_does_not(string file, string slug)
    {
        var challenge = Lesson(file, slug).GetProperty("challenge");
        var tests = challenge.GetProperty("tests").EnumerateArray()
            .Select((t, i) => new TestSpec(i + 1, t.GetProperty("label").GetString()!, t.GetProperty("kind").GetString()!,
                t.GetProperty("spec").Clone(), t.GetProperty("failMessage").GetString()!))
            .ToList();
        Assert.Empty(tests.Where(t => !TestEvaluator.Kinds.Contains(t.Kind)).Select(t => $"{t.Label}: unknown kind {t.Kind}"));

        var solution = await CSharpCodeRunner.RunAsync(new RunRequest(Files(challenge.GetProperty("solutionFiles")), tests));
        var failing = solution.Results.Where(r => !r.Passed).Select(r => $"{tests[r.TestId - 1].Label}: {r.Message}");
        Assert.True(!failing.Any(), $"{file} → {slug}: the solution fails: {string.Join("; ", failing)}. Output: {string.Join(" | ", solution.Console.Select(l => l.Text))}");

        var starter = await CSharpCodeRunner.RunAsync(new RunRequest(Files(challenge.GetProperty("starterFiles")), tests));
        Assert.True(starter.Results.Any(r => !r.Passed), $"{file} → {slug}: the starter code already passes every test.");
    }

    [Theory]
    [MemberData(nameof(CSharpLessons))]
    public async Task Worked_example_runs_cleanly(string file, string slug)
    {
        var example = Lesson(file, slug).GetProperty("exampleCode").GetString();
        Assert.False(string.IsNullOrWhiteSpace(example), $"{file} → {slug}: C# lessons need an exampleCode (a whole Program.cs).");

        var result = await CSharpCodeRunner.RunAsync(new RunRequest(new() { ["Program.cs"] = example! }));
        Assert.True(result.Stopped == StopReason.None, $"{file} → {slug}: the example stopped ({result.Stopped}): {string.Join(" | ", result.Console.Select(l => l.Text))}");
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == "warning").Select(d => $"line {d.Line}: {d.Message}"));
        if (result.Server is { } server)
        {
            // A web server prints nothing: it waits for requests. It must start and have endpoints to try.
            Assert.True(server.Running, $"{file} → {slug}: the example builds a web app but never calls app.Run().");
            Assert.NotEmpty(server.Endpoints);
        }
        else
        {
            Assert.NotEmpty(result.Output);
        }
    }
}
