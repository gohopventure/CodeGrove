using System.Text.Json;
using System.Text.Json.Serialization;
using Codegrove.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Codegrove.Api.Data.Seeding;

// ---- File shapes (Content/*.json) ---------------------------------------------------------------
// Content lives in the repo as JSON until the Phase 2 authoring UI exists. Authors edit files,
// open a pull request, and the seeder upserts by slug on deploy.

public sealed record CatalogFile(List<TrackFile> Tracks, List<BadgeFile> Badges);

public sealed record TrackFile(
    string Slug, string Title, string Description, AgeBand AgeBand, int SortOrder, bool IsPublished,
    List<string> Courses, List<string>? Prerequisites);

public sealed record BadgeFile(
    string Code, string Name, string Description, string IconUrl, BadgeRuleType RuleType, int? RuleValue, string? RuleCourseSlug);

public sealed record CourseFile(
    string Slug, string Title, string Summary, AgeBand AgeBand, int EstimatedMinutes, bool IsPublished,
    List<string> Objectives, List<LessonFile> Lessons);

public sealed record LessonFile(
    string Slug, string Title, int SortOrder, AgeBand AgeBand, int EstimatedMinutes, bool IsPublished,
    string? LearnMarkdown, string? ExampleMarkdown, string? ExampleCode, string? VideoUrl, string? CaptionsUrl,
    ChallengeFile? Challenge, QuizFile? Quiz);

public sealed record ChallengeFile(
    ChallengeKind Kind, string InstructionsMarkdown, Dictionary<string, string> StarterFiles,
    Dictionary<string, string> SolutionFiles, int XpReward, List<TestFile> Tests, List<HintFile> Hints);

public sealed record TestFile(string Label, string Kind, JsonElement Spec, string FailMessage);

public sealed record HintFile(int Level, string Markdown);

public sealed record QuizFile(int PassPercent, List<QuestionFile> Questions);

public sealed record QuestionFile(string Prompt, string Explanation, List<OptionFile> Options);

public sealed record OptionFile(string Text, bool Correct);

/// <summary>
/// Loads Content/catalog.json and Content/courses/*.json and upserts them. Safe to run on every start:
/// existing rows are updated in place so learner progress (keyed by lesson id) is preserved.
/// Test cases and quiz questions are replaced wholesale; hints are upserted by level because
/// hint reveals reference them.
/// </summary>
public sealed class ContentSeeder(AppDbContext db, ILogger<ContentSeeder> logger)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public async Task SeedAsync(string contentRoot, CancellationToken ct = default)
    {
        var courseFiles = Directory.GetFiles(Path.Combine(contentRoot, "courses"), "*.json").Order().ToList();
        foreach (var path in courseFiles)
        {
            var file = await Read<CourseFile>(path, ct);
            Validate(file, path);
            await UpsertCourseAsync(file, ct);
        }

        var catalog = await Read<CatalogFile>(Path.Combine(contentRoot, "catalog.json"), ct);
        await UpsertTracksAsync(catalog.Tracks, ct);
        await UpsertBadgesAsync(catalog.Badges, ct);

        logger.LogInformation("Seeded {Courses} courses, {Tracks} tracks, {Badges} badges",
            courseFiles.Count, catalog.Tracks.Count, catalog.Badges.Count);
    }

    private async Task UpsertCourseAsync(CourseFile f, CancellationToken ct)
    {
        var course = await db.Courses
            .Include(c => c.Lessons).ThenInclude(l => l.Challenge!).ThenInclude(c => c.TestCases)
            .Include(c => c.Lessons).ThenInclude(l => l.Challenge!).ThenInclude(c => c.Hints)
            .Include(c => c.Lessons).ThenInclude(l => l.Quiz!).ThenInclude(q => q.Questions).ThenInclude(q => q.Options)
            .AsSplitQuery()
            .SingleOrDefaultAsync(c => c.Slug == f.Slug, ct);

        if (course is null)
        {
            course = new Course { Slug = f.Slug };
            db.Courses.Add(course);
        }

        course.Title = f.Title;
        course.Summary = f.Summary;
        course.AgeBand = f.AgeBand;
        course.EstimatedMinutes = f.EstimatedMinutes;
        course.IsPublished = f.IsPublished;
        course.ObjectivesJson = JsonSerializer.Serialize(f.Objectives);

        foreach (var lf in f.Lessons)
        {
            var lesson = course.Lessons.SingleOrDefault(l => l.Slug == lf.Slug);
            if (lesson is null)
            {
                lesson = new Lesson { Slug = lf.Slug };
                course.Lessons.Add(lesson);
            }

            lesson.Title = lf.Title;
            lesson.SortOrder = lf.SortOrder;
            lesson.AgeBand = lf.AgeBand;
            lesson.EstimatedMinutes = lf.EstimatedMinutes;
            lesson.IsPublished = lf.IsPublished;
            lesson.LearnMarkdown = lf.LearnMarkdown ?? "";
            lesson.ExampleMarkdown = lf.ExampleMarkdown ?? "";
            lesson.ExampleCode = lf.ExampleCode;
            lesson.VideoUrl = lf.VideoUrl;
            lesson.CaptionsUrl = lf.CaptionsUrl;

            UpsertChallenge(lesson, lf.Challenge);
            UpsertQuiz(lesson, lf.Quiz);
        }

        await db.SaveChangesAsync(ct);
    }

    private void UpsertChallenge(Lesson lesson, ChallengeFile? f)
    {
        if (f is null)
        {
            if (lesson.Challenge is not null) db.Challenges.Remove(lesson.Challenge);
            return;
        }

        var c = lesson.Challenge ??= new Challenge();
        c.Kind = f.Kind;
        c.InstructionsMarkdown = f.InstructionsMarkdown;
        c.StarterFilesJson = JsonSerializer.Serialize(f.StarterFiles);
        c.SolutionFilesJson = JsonSerializer.Serialize(f.SolutionFiles);
        c.XpReward = f.XpReward;

        db.TestCases.RemoveRange(c.TestCases);
        c.TestCases = f.Tests.Select((t, i) => new TestCase
        {
            SortOrder = i + 1, Label = t.Label, Kind = t.Kind, SpecJson = t.Spec.GetRawText(), FailMessage = t.FailMessage,
        }).ToList();

        foreach (var hf in f.Hints)
        {
            var hint = c.Hints.SingleOrDefault(h => h.Level == hf.Level);
            if (hint is null) c.Hints.Add(new Hint { Level = hf.Level, Markdown = hf.Markdown });
            else hint.Markdown = hf.Markdown;
        }
    }

    private void UpsertQuiz(Lesson lesson, QuizFile? f)
    {
        if (f is null)
        {
            if (lesson.Quiz is not null) db.Quizzes.Remove(lesson.Quiz);
            return;
        }

        var quiz = lesson.Quiz ??= new Quiz();
        quiz.PassPercent = f.PassPercent;

        foreach (var q in quiz.Questions) db.QuizOptions.RemoveRange(q.Options);
        db.QuizQuestions.RemoveRange(quiz.Questions);
        quiz.Questions = f.Questions.Select((q, i) => new QuizQuestion
        {
            SortOrder = i + 1,
            Prompt = q.Prompt,
            Explanation = q.Explanation,
            Options = q.Options.Select((o, j) => new QuizOption { SortOrder = j + 1, Text = o.Text, IsCorrect = o.Correct }).ToList(),
        }).ToList();
    }

    private async Task UpsertTracksAsync(List<TrackFile> files, CancellationToken ct)
    {
        var courses = await db.Courses.ToDictionaryAsync(c => c.Slug, ct);

        foreach (var f in files)
        {
            var track = await db.Tracks.Include(t => t.Courses).SingleOrDefaultAsync(t => t.Slug == f.Slug, ct);
            if (track is null)
            {
                track = new Track { Slug = f.Slug };
                db.Tracks.Add(track);
            }

            track.Title = f.Title;
            track.Description = f.Description;
            track.AgeBand = f.AgeBand;
            track.SortOrder = f.SortOrder;
            track.IsPublished = f.IsPublished;

            db.TrackCourses.RemoveRange(track.Courses);
            track.Courses = f.Courses.Select((slug, i) => new TrackCourse
            {
                Course = courses.TryGetValue(slug, out var c) ? c : throw new InvalidDataException($"Track '{f.Slug}' references unknown course '{slug}'."),
                SortOrder = i + 1,
            }).ToList();
        }
        await db.SaveChangesAsync(ct);

        // Prerequisites need every track to exist first.
        var tracks = await db.Tracks.Include(t => t.Prerequisites).ToDictionaryAsync(t => t.Slug, ct);
        foreach (var f in files)
        {
            var track = tracks[f.Slug];
            db.TrackPrerequisites.RemoveRange(track.Prerequisites);
            track.Prerequisites = (f.Prerequisites ?? [])
                .Select(slug => new TrackPrerequisite { RequiresTrack = tracks[slug] })
                .ToList();
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task UpsertBadgesAsync(List<BadgeFile> files, CancellationToken ct)
    {
        foreach (var f in files)
        {
            var badge = await db.Badges.SingleOrDefaultAsync(b => b.Code == f.Code, ct);
            if (badge is null)
            {
                badge = new Badge { Code = f.Code };
                db.Badges.Add(badge);
            }

            badge.Name = f.Name;
            badge.Description = f.Description;
            badge.IconUrl = f.IconUrl;
            badge.RuleType = f.RuleType;
            badge.RuleValue = f.RuleCourseSlug is not null
                ? await db.Courses.Where(c => c.Slug == f.RuleCourseSlug).Select(c => c.Id).SingleAsync(ct)
                : f.RuleValue ?? 1;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Catches authoring mistakes at seed time instead of in front of a child.</summary>
    private static void Validate(CourseFile f, string path)
    {
        foreach (var l in f.Lessons.Where(l => l.IsPublished))
        {
            void Fail(string msg) => throw new InvalidDataException($"{Path.GetFileName(path)} → lesson '{l.Slug}': {msg}");

            if (string.IsNullOrWhiteSpace(l.LearnMarkdown)) Fail("published lessons need learn content.");
            if (l.VideoUrl is not null && l.CaptionsUrl is null) Fail("videos must have a captions file (WCAG).");
            if (l.Challenge is { } c)
            {
                if (c.Tests.Count == 0) Fail("a challenge needs at least one test.");
                if (c.Hints.Select(h => h.Level).Order().SequenceEqual([1, 2, 3]) is false) Fail("a challenge needs hints at levels 1, 2 and 3.");
            }
            if (l.Quiz is { } q && q.Questions.Any(x => x.Options.Count(o => o.Correct) != 1))
                Fail("every quiz question needs exactly one correct option.");
        }
    }

    private static async Task<T> Read<T>(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct)
               ?? throw new InvalidDataException($"Empty content file: {path}");
    }
}
