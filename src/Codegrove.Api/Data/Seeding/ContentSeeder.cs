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

/// <summary>
/// Content/tutor/&lt;course&gt;.json: the tutor's intro and recap for each lesson of one course, keyed by
/// lesson slug. Kept apart from the course files so scripts can be written and revised (and later voiced)
/// on their own. Each list is one or more segments, in order.
/// </summary>
public sealed record TutorScriptFile(string Course, Dictionary<string, LessonScriptFile> Lessons);

public sealed record LessonScriptFile(List<SpokenLineFile> Intro, List<SpokenLineFile> Recap);

/// <summary>
/// One thing the tutor says, written twice: <paramref name="DisplayText"/> for the screen (may use `code`) and
/// <paramref name="SpeechText"/> for the voice (plain words only).
/// </summary>
public sealed record SpokenLineFile(string DisplayText, string SpeechText, TutorMood Mood);

/// <summary>Content/tutors.json: one tutor character per learner band, with its reusable event lines.</summary>
public sealed record TutorsFile(List<TutorCharacterFile> Characters);

/// <summary><paramref name="Lines"/> is keyed by <see cref="TutorEvent"/> name, e.g. "pass".</summary>
public sealed record TutorCharacterFile(
    string Slug, string Name, AgeBand Band, string VoiceId, string PortraitUrl, string PersonaPrompt,
    Dictionary<string, List<SpokenLineFile>> Lines);

public sealed record BadgeFile(
    string Code, string Name, string Description, string IconUrl, BadgeRuleType RuleType, int? RuleValue, string? RuleCourseSlug);

public sealed record CourseFile(
    string Slug, string Title, string Summary, AgeBand AgeBand, int EstimatedMinutes, bool IsPublished,
    List<string> Objectives, List<LessonFile> Lessons);

public sealed record LessonFile(
    string Slug, string Title, int SortOrder, AgeBand AgeBand, int EstimatedMinutes, bool IsPublished,
    string? LearnMarkdown, string? ExampleMarkdown, string? ExampleCode, string? VideoUrl, string? CaptionsUrl,
    ChallengeFile? Challenge, QuizFile? Quiz);

/// <summary>
/// <paramref name="ContinuesFrom"/> (capstone milestones): the slug of an earlier lesson in the same course and band.
/// The learner starts from their own passing code for that challenge, for the files in <paramref name="CarryFiles"/>.
/// Each carried file in <paramref name="StarterFiles"/> must equal that lesson's solution: that's the reference
/// a learner without passing code starts from.
/// </summary>
public sealed record ChallengeFile(
    ChallengeKind Kind, string InstructionsMarkdown, Dictionary<string, string> StarterFiles,
    Dictionary<string, string> SolutionFiles, int XpReward, List<TestFile> Tests, List<HintFile> Hints, BlocksFile? Blocks = null,
    string? ContinuesFrom = null, List<string>? CarryFiles = null);

/// <summary>
/// Blocks mode for a JavaScript challenge. Toolbox entries are category keys the web app knows
/// (see BlockCategories). Starter and Solution are Blockly workspace states; only the starter is stored.
/// The web app's content check runs the solution's generated code against the challenge's tests.
/// </summary>
public sealed record BlocksFile(List<string> Toolbox, JsonElement? Starter, JsonElement? Solution);

public sealed record TestFile(string Label, string Kind, JsonElement Spec, string FailMessage);

/// <summary>A hint: markdown for the screen, plus speechText and mood for the tutor's voice.</summary>
public sealed record HintFile(int Level, string Markdown, string? SpeechText = null, TutorMood? Mood = null);

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

        var tutorDir = Path.Combine(contentRoot, "tutor");
        var tutorFiles = Directory.Exists(tutorDir) ? Directory.GetFiles(tutorDir, "*.json").Order().ToList() : [];
        foreach (var path in tutorFiles)
            await UpsertTutorScriptsAsync(await Read<TutorScriptFile>(path, ct), path, ct);

        // Every published lesson needs an intro and a recap. Unpublished lessons (a course in progress) don't.
        var unscripted = await db.Lessons
            .Where(l => l.IsPublished && l.Course.IsPublished
                && (!l.Scripts.Any(s => s.Kind == LessonScriptKind.Intro) || !l.Scripts.Any(s => s.Kind == LessonScriptKind.Recap)))
            .Select(l => l.Course.Slug + "/" + l.Slug).ToListAsync(ct);
        if (unscripted.Count > 0)
            throw new InvalidDataException(
                $"{unscripted.Count} published lessons have no tutor intro/recap in Content/tutor: {string.Join(", ", unscripted.Take(10))}");

        await UpsertTutorsAsync(await Read<TutorsFile>(Path.Combine(contentRoot, "tutors.json"), ct), ct);

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

    /// <summary>Replaces the tutor scripts of one course's lessons with the file's contents.</summary>
    private async Task UpsertTutorScriptsAsync(TutorScriptFile f, string path, CancellationToken ct)
    {
        InvalidDataException Bad(string msg) => new($"{Path.GetFileName(path)}: {msg}");

        var lessons = await db.Lessons.Include(l => l.Scripts)
            .Where(l => l.Course.Slug == f.Course).ToListAsync(ct);
        if (lessons.Count == 0) throw Bad($"unknown course '{f.Course}'.");

        foreach (var slug in f.Lessons.Keys.Where(k => lessons.All(l => l.Slug != k)))
            throw Bad($"lesson '{slug}' isn't in course '{f.Course}'.");

        foreach (var lesson in lessons)
        {
            db.LessonScripts.RemoveRange(lesson.Scripts);
            lesson.Scripts.Clear();
            if (!f.Lessons.TryGetValue(lesson.Slug, out var script)) continue;

            foreach (var (kind, lines) in new[] { (LessonScriptKind.Intro, script.Intro), (LessonScriptKind.Recap, script.Recap) })
            {
                var name = kind.ToString().ToLowerInvariant();
                if (lines is null || lines.Count == 0) throw Bad($"lesson '{lesson.Slug}' needs a non-empty {name}.");

                // Length rules apply to the whole intro or recap, however many segments it's split into.
                var (min, max) = TutorRules.ScriptSentences(lesson.AgeBand);
                var problem = lines.Select(l => TutorRules.CheckDisplay(l.DisplayText) ?? TutorRules.CheckSpeech(l.SpeechText, 1, max)).FirstOrDefault(p => p is not null)
                    ?? TutorRules.CheckSpeech(string.Join(" ", lines.Select(l => l.SpeechText)), min, max);
                if (problem is not null) throw Bad($"lesson '{lesson.Slug}' {name}: {problem}");

                for (var i = 0; i < lines.Count; i++)
                    lesson.Scripts.Add(new LessonScript
                    {
                        Kind = kind, SortOrder = i, Mood = lines[i].Mood,
                        DisplayText = lines[i].DisplayText.Trim(), SpeechText = lines[i].SpeechText.Trim(),
                    });
            }
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Minimum and maximum pool sizes per event. Pass has the most lines so praise doesn't repeat.</summary>
    private static readonly IReadOnlyDictionary<TutorEvent, (int Min, int Max)> PoolSizes = new Dictionary<TutorEvent, (int, int)>
    {
        [TutorEvent.FirstFail] = (3, 10),
        [TutorEvent.StuckOffer] = (3, 10),
        [TutorEvent.Pass] = (8, 10),
    };

    /// <summary>Upserts the tutor characters by slug and replaces their event lines.</summary>
    private async Task UpsertTutorsAsync(TutorsFile f, CancellationToken ct)
    {
        static InvalidDataException Bad(string msg) => new($"tutors.json: {msg}");

        foreach (var band in new[] { AgeBand.Explorer, AgeBand.Builder })
            if (f.Characters.Count(c => c.Band == band) != 1) throw Bad($"needs exactly one {band} tutor.");
        if (f.Characters.FirstOrDefault(c => c.Band is not (AgeBand.Explorer or AgeBand.Builder)) is { } odd)
            throw Bad($"tutor '{odd.Slug}' must be for Explorer or Builder.");

        var existing = await db.TutorCharacters.Include(c => c.Lines).ToListAsync(ct);
        // Band is unique: drop characters that are no longer in the file before adding a replacement.
        foreach (var gone in existing.Where(e => f.Characters.All(c => c.Slug != e.Slug)))
            db.TutorCharacters.Remove(gone);
        await db.SaveChangesAsync(ct);

        foreach (var cf in f.Characters)
        {
            if (string.IsNullOrWhiteSpace(cf.Name) || !cf.PortraitUrl.Contains("{mood}"))
                throw Bad($"tutor '{cf.Slug}' needs a name and a portraitUrl with a {{mood}} placeholder.");

            var character = existing.SingleOrDefault(e => e.Slug == cf.Slug);
            if (character is null)
            {
                character = new TutorCharacter { Slug = cf.Slug };
                db.TutorCharacters.Add(character);
            }
            character.Name = cf.Name;
            character.Band = cf.Band;
            character.VoiceId = cf.VoiceId;
            character.PortraitUrl = cf.PortraitUrl;
            character.PersonaPrompt = cf.PersonaPrompt;

            db.TutorLines.RemoveRange(character.Lines);
            character.Lines.Clear();
            foreach (var (key, lines) in cf.Lines)
            {
                if (!Enum.TryParse<TutorEvent>(key, ignoreCase: true, out var ev) || !Enum.IsDefined(ev))
                    throw Bad($"tutor '{cf.Slug}': unknown event '{key}'. Known: {string.Join(", ", Enum.GetNames<TutorEvent>())}.");
                for (var i = 0; i < lines.Count; i++)
                {
                    var line = lines[i];
                    if ((TutorRules.CheckDisplay(line.DisplayText) ?? TutorRules.CheckSpeech(line.SpeechText, 1, TutorRules.MaxLineSentences(cf.Band))) is { } p)
                        throw Bad($"tutor '{cf.Slug}' {key} line {i + 1}: {p}");
                    // StuckOffer only offers: it asks, it never gives the hint itself.
                    if (ev == TutorEvent.StuckOffer && !line.SpeechText.TrimEnd().EndsWith('?'))
                        throw Bad($"tutor '{cf.Slug}' stuckOffer line {i + 1} should be a question that offers a hint.");
                    character.Lines.Add(new TutorLine
                    {
                        Event = ev, SortOrder = i, Mood = line.Mood,
                        DisplayText = line.DisplayText.Trim(), SpeechText = line.SpeechText.Trim(),
                    });
                }
            }
            foreach (var (ev, (min, max)) in PoolSizes)
            {
                var n = character.Lines.Count(l => l.Event == ev);
                if (n < min || n > max) throw Bad($"tutor '{cf.Slug}' needs {min}–{max} {ev} lines, not {n}.");
            }
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
        c.BlocksJson = f.Blocks is { } bf
            ? JsonSerializer.Serialize(new { toolbox = bf.Toolbox, starter = bf.Starter }, Json)
            : null;
        c.ContinuesFromLessonSlug = f.ContinuesFrom;
        c.CarryFilesJson = f.CarryFiles is { Count: > 0 } carry ? JsonSerializer.Serialize(carry) : null;

        db.TestCases.RemoveRange(c.TestCases);
        c.TestCases = f.Tests.Select((t, i) => new TestCase
        {
            SortOrder = i + 1, Label = t.Label, Kind = t.Kind, SpecJson = t.Spec.GetRawText(), FailMessage = t.FailMessage,
        }).ToList();

        foreach (var hf in f.Hints)
        {
            var hint = c.Hints.SingleOrDefault(h => h.Level == hf.Level);
            if (hint is null) c.Hints.Add(hint = new Hint { Level = hf.Level });
            hint.Markdown = hf.Markdown;
            hint.SpeechText = hf.SpeechText?.Trim() ?? "";
            hint.Mood = hf.Mood ?? TutorMood.Thinking;
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

    /// <summary>Toolbox categories the web app's blocks editor defines (web/src/lesson/blocks/toolbox.ts).</summary>
    public static readonly IReadOnlySet<string> BlockCategories =
        new HashSet<string> { "variables", "math", "text", "logic", "loops", "functions", "console" };

    /// <summary>Test kinds the C# runner understands (src/Codegrove.CSharpRunner/TestEvaluator.cs).</summary>
    public static readonly IReadOnlySet<string> CSharpTestKinds =
        new HashSet<string> { "noErrors", "consoleIncludes", "consoleEquals", "compileError", "compileWarning", "noWarnings", "csharpExpression", "sourceMatches", "httpRequest" };

    /// <summary>Test kinds the SQL runner understands (web/src/runner/sqlEngine.ts, SQL_TEST_KINDS).</summary>
    public static readonly IReadOnlySet<string> SqlTestKinds =
        new HashSet<string> { "noErrors", "sourceMatches", "sqlResultEquals", "sqlRowCount", "sqlCheckQuery", "sqlError" };

    /// <summary>
    /// Page test kinds a FullStack challenge can run in its preview (web/src/runner/testEngine.ts), against
    /// the learner's live API. The C# kinds run against the api/ files instead (web/src/runner/fullStack.ts).
    /// </summary>
    public static readonly IReadOnlySet<string> FullStackWebTestKinds =
        new HashSet<string> { "elementExists", "elementCount", "elementText", "attribute", "cssProperty", "cssRule", "jsExpression", "interact", "consoleIncludes", "noErrors" };

    /// <summary>Kinds both halves of a FullStack challenge have: their spec says which with "side": "api" or "web".</summary>
    public static readonly IReadOnlySet<string> FullStackSharedTestKinds = new HashSet<string> { "noErrors", "consoleIncludes" };

    /// <summary>The hidden starter file that builds a SQL challenge's database (sqlEngine.ts, SETUP_FILE).</summary>
    public const string SqlSetupFile = ".setup.sql";

    /// <summary>Catches authoring mistakes at seed time instead of in front of a child.</summary>
    private static void Validate(CourseFile f, string path)
    {
        foreach (var l in f.Lessons.Where(l => l.IsPublished))
        {
            void Fail(string msg) => throw new InvalidDataException($"{Path.GetFileName(path)} → lesson '{l.Slug}': {msg}");

            if (string.IsNullOrWhiteSpace(l.LearnMarkdown)) Fail("published lessons need learn content.");

            // Capstone milestones carry the learner's own files forward. The reference they get instead (no
            // passing code yet) is this starter, so each carried file must be the previous milestone's solution.
            if (l.Challenge is { ContinuesFrom: { } fromSlug } carrying)
            {
                var from = f.Lessons.FirstOrDefault(x => x.Slug == fromSlug);
                if (from?.Challenge is null) Fail($"continuesFrom '{fromSlug}' isn't a lesson with a challenge in this course.");
                else
                {
                    if (from.AgeBand != l.AgeBand) Fail($"continuesFrom '{fromSlug}' is for {from.AgeBand}, not {l.AgeBand}.");
                    if (from.SortOrder >= l.SortOrder) Fail($"continuesFrom '{fromSlug}' must come earlier in the course.");
                    if (carrying.CarryFiles is not { Count: > 0 } carry) Fail("continuesFrom needs carryFiles.");
                    else
                    {
                        foreach (var name in carry)
                        {
                            if (!carrying.StarterFiles.TryGetValue(name, out var starter)) Fail($"carried file '{name}' isn't in the starter.");
                            else if (!from.Challenge.SolutionFiles.TryGetValue(name, out var previous)) Fail($"carried file '{name}' isn't in {fromSlug}'s solution.");
                            else if (starter != previous) Fail($"carried file '{name}' must start as {fromSlug}'s solution.");
                        }
                    }
                }
            }
            else if (l.Challenge?.CarryFiles is { Count: > 0 }) Fail("carryFiles needs continuesFrom.");
            if (l.VideoUrl is not null && l.CaptionsUrl is null) Fail("videos must have a captions file (WCAG).");
            if (l.Challenge is { } c)
            {
                if (c.Blocks is { } b)
                {
                    if (c.Kind != ChallengeKind.Js) Fail("blocks mode is only for JavaScript challenges.");
                    if (b.Toolbox.Count == 0) Fail("blocks mode needs at least one toolbox category.");
                    if (b.Toolbox.FirstOrDefault(k => !BlockCategories.Contains(k)) is { } bad)
                        Fail($"unknown blocks category '{bad}'. Known: {string.Join(", ", BlockCategories)}.");
                }
                if (c.Tests.Count == 0) Fail("a challenge needs at least one test.");
                // Angular is written in TypeScript, so Angular challenges follow the TypeScript rules too.
                if (c.Kind is ChallengeKind.TypeScript or ChallengeKind.Angular)
                {
                    static bool HasTs(Dictionary<string, string> files) => files.Keys.Any(k => !k.StartsWith('.') && (k.EndsWith(".ts") || k.EndsWith(".tsx")));
                    if (!HasTs(c.StarterFiles) || !HasTs(c.SolutionFiles)) Fail($"{c.Kind} challenges need at least one .ts file.");
                    // TypeScript's whole job is finding type errors; a lesson that never checks them isn't teaching it.
                    if (!c.Tests.Any(t => t.Kind is "typeCheck" or "typeError")) Fail($"{c.Kind} challenges need a typeCheck or typeError test.");
                }
                else if (c.Tests.Any(t => t.Kind is "typeCheck" or "typeError"))
                    Fail("typeCheck and typeError tests only work in TypeScript and Angular challenges.");
                // FullStack (capstone): an ASP.NET Core API in api/ and a React page in web/ that calls it. Each
                // test runs on one side, decided by its kind (see web/src/runner/fullStack.ts, splitTests).
                if (c.Kind == ChallengeKind.FullStack)
                {
                    foreach (var (name, files) in new[] { ("starter", c.StarterFiles), ("solution", c.SolutionFiles) })
                    {
                        if (files.Keys.FirstOrDefault(k => !k.StartsWith("api/") && !k.StartsWith("web/")) is { } stray)
                            Fail($"FullStack {name} file '{stray}' must be in api/ or web/.");
                        if (!files.Any(f => f.Key.StartsWith("api/") && f.Key.EndsWith(".cs") && f.Value.Contains("WebApplication")))
                            Fail($"FullStack {name} files need an api/*.cs file that builds a WebApplication.");
                        if (!files.ContainsKey("web/index.html")) Fail($"FullStack {name} files need web/index.html.");
                    }
                    foreach (var t in c.Tests)
                    {
                        var side = t.Spec.ValueKind == JsonValueKind.Object && t.Spec.TryGetProperty("side", out var sv) ? sv.GetString() : null;
                        if (FullStackSharedTestKinds.Contains(t.Kind))
                        {
                            if (side is not ("api" or "web")) Fail($"test '{t.Label}' ({t.Kind}) needs \"side\": \"api\" or \"web\".");
                        }
                        else if (side is not null) Fail($"test '{t.Label}': only noErrors and consoleIncludes take a side.");
                        else if (t.Kind == "sourceMatches")
                        {
                            if (!(t.Spec.TryGetProperty("file", out var file) && file.GetString() is { } fn && fn.StartsWith("api/")))
                                Fail($"test '{t.Label}': in FullStack challenges, sourceMatches needs a \"file\" in api/.");
                        }
                        else if (!CSharpTestKinds.Contains(t.Kind) && !FullStackWebTestKinds.Contains(t.Kind))
                            Fail($"test '{t.Label}' has kind '{t.Kind}', which FullStack challenges can't use.");
                    }
                    if (l.ExampleCode is { Length: > 0 } example)
                    {
                        Dictionary<string, string>? exampleFiles = null;
                        try { exampleFiles = JsonSerializer.Deserialize<Dictionary<string, string>>(example); } catch (JsonException) { }
                        if (exampleFiles is null || !exampleFiles.ContainsKey("web/index.html") || !exampleFiles.Keys.Any(k => k.StartsWith("api/")))
                            Fail("a FullStack lesson's exampleCode must be a JSON object of files with api/ files and web/index.html.");
                    }
                }
                // C# runs in the in-browser C# runner (src/Codegrove.CSharpRunner), which has its own test kinds.
                else if (c.Kind == ChallengeKind.CSharp)
                {
                    static bool HasCs(Dictionary<string, string> files) => files.Keys.Any(k => !k.StartsWith('.') && k.EndsWith(".cs"));
                    if (!HasCs(c.StarterFiles) || !HasCs(c.SolutionFiles)) Fail("C# challenges need at least one .cs file.");
                    if (c.Tests.FirstOrDefault(t => !CSharpTestKinds.Contains(t.Kind)) is { } bad)
                        Fail($"test '{bad.Label}' has kind '{bad.Kind}', which C# challenges can't use. Known: {string.Join(", ", CSharpTestKinds)}.");
                }
                else if (c.Kind != ChallengeKind.FullStack && c.Tests.FirstOrDefault(t => t.Kind is "consoleEquals" or "compileError" or "compileWarning" or "noWarnings" or "csharpExpression" or "httpRequest") is { } csOnly)
                    Fail($"test '{csOnly.Label}' has kind '{csOnly.Kind}', which only works in C# challenges.");
                // SQL runs in the in-browser SQL runner (web/src/runner/sqlEngine.ts): SQLite, starting every run
                // from the hidden .setup.sql in the starter files, which the learner can't see or edit.
                if (c.Kind == ChallengeKind.Sql)
                {
                    static bool HasSql(Dictionary<string, string> files) => files.Keys.Any(k => !k.StartsWith('.') && k.EndsWith(".sql"));
                    if (!HasSql(c.StarterFiles) || !HasSql(c.SolutionFiles)) Fail("SQL challenges need at least one .sql file.");
                    if (!c.StarterFiles.TryGetValue(SqlSetupFile, out var setup) || string.IsNullOrWhiteSpace(setup))
                        Fail($"SQL challenges need a {SqlSetupFile} starter file that builds the database.");
                    if (c.Tests.FirstOrDefault(t => !SqlTestKinds.Contains(t.Kind)) is { } bad)
                        Fail($"test '{bad.Label}' has kind '{bad.Kind}', which SQL challenges can't use. Known: {string.Join(", ", SqlTestKinds)}.");
                }
                else if (c.Tests.FirstOrDefault(t => t.Kind.StartsWith("sql")) is { } sqlOnly)
                    Fail($"test '{sqlOnly.Label}' has kind '{sqlOnly.Kind}', which only works in SQL challenges.");
                if (c.Kind is not (ChallengeKind.CSharp or ChallengeKind.Sql or ChallengeKind.FullStack) && c.Tests.FirstOrDefault(t => t.Kind == "sourceMatches") is { } src)
                    Fail($"test '{src.Label}' has kind 'sourceMatches', which only works in C# and SQL challenges.");
                if (c.Hints.Select(h => h.Level).Order().SequenceEqual([1, 2, 3]) is false) Fail("a challenge needs hints at levels 1, 2 and 3.");
                foreach (var h in c.Hints)
                {
                    if (h.Mood is null) Fail($"hint {h.Level} needs a mood.");
                    if (TutorRules.CheckHint(h.Level, h.Markdown, h.SpeechText, l.AgeBand, c.SolutionFiles) is { } bad) Fail(bad);
                }
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
