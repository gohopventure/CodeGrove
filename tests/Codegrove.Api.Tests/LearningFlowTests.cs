using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Codegrove.Api.Data.Seeding;

namespace Codegrove.Api.Tests;

/// <summary>
/// End-to-end: a parent signs up, adds an under-13 child, confirms consent, and the child completes
/// the seeded first HTML lesson. Exercises the real HTTP pipeline, auth cookies, policies and EF.
/// </summary>
public class LearningFlowTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private static readonly int ThisYear = DateTime.UtcNow.Year;

    public LearningFlowTests(ApiFactory factory) => _factory = factory;

    private async Task<(HttpClient Parent, HttpClient Child, string ChildId)> FamilyWithConsentedExplorer(string childName)
    {
        var parent = _factory.NewClient();
        var reg = await parent.PostJson("/api/auth/register-parent",
            new { email = $"{childName.ToLowerInvariant()}-parent@example.com", password = "parent-pass-1", confirmAdult = true });
        Assert.Equal(HttpStatusCode.OK, reg.StatusCode);

        var created = await parent.PostJson("/api/parent/children",
            new { userName = childName, password = "kid-password", birthYear = ThisYear - 10, birthMonth = 1 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Json();
        Assert.True(body.GetProperty("consentRequired").GetBoolean());
        var childId = body.GetProperty("child").GetProperty("id").GetString()!;

        // Before consent the child can't sign in.
        var child = _factory.NewClient();
        var early = await child.PostJson("/api/auth/login", new { login = childName, password = "kid-password" });
        Assert.Equal(HttpStatusCode.Forbidden, early.StatusCode);

        // The parent confirms using the token from the (dev-echoed) email link.
        var url = new Uri(body.GetProperty("devConfirmUrl").GetString()!);
        var token = Uri.UnescapeDataString(url.Query.Split("token=")[1]);
        var confirm = await parent.PostJson("/api/consent/confirm", new { token, agreeToNotice = true });
        Assert.Equal(HttpStatusCode.NoContent, confirm.StatusCode);

        // The token is single-use.
        var again = await parent.PostJson("/api/consent/confirm", new { token, agreeToNotice = true });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);

        var login = await child.PostJson("/api/auth/login", new { login = childName, password = "kid-password" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (parent, child, childId);
    }

    private static async Task<JsonElement> ExplorerLesson(HttpClient child)
    {
        var course = await child.GetJson("/api/courses/html-basics");
        var lessons = course.GetProperty("lessons").EnumerateArray().ToList();

        // Explorers see the Explorer version of lesson 1, never the Builder one.
        Assert.Contains(lessons, l => l.GetProperty("slug").GetString() == "tags-are-labels");
        Assert.DoesNotContain(lessons, l => l.GetProperty("slug").GetString() == "tags-and-elements");

        var id = lessons.First(l => l.GetProperty("slug").GetString() == "tags-are-labels").GetProperty("id").GetInt32();
        return await child.GetJson($"/api/lessons/{id}");
    }

    [Fact]
    public async Task Child_completes_first_lesson_and_earns_xp_and_a_badge_once()
    {
        var (parent, child, childId) = await FamilyWithConsentedExplorer("BraveOtter11");
        var lesson = await ExplorerLesson(child);

        var challenge = lesson.GetProperty("challenge");
        var challengeId = challenge.GetProperty("id").GetInt32();
        var testIds = challenge.GetProperty("tests").EnumerateArray().Select(t => t.GetProperty("id").GetInt32()).ToList();
        Assert.Equal(5, testIds.Count);
        Assert.Equal(JsonValueKind.Null, challenge.GetProperty("solutionFiles").ValueKind); // no solution up front

        var files = new Dictionary<string, string> { ["index.html"] = "<h1>Welcome to my page</h1><p>Octopuses have three hearts.</p>" };

        // One failing test → not passed, no XP.
        var partial = testIds.Select((id, i) => new { testId = id, passed = i != 4, message = (string?)null });
        var fail = await (await child.PostJson($"/api/challenges/{challengeId}/submissions", new { files, results = partial })).Json();
        Assert.False(fail.GetProperty("passed").GetBoolean());
        Assert.Equal(0, fail.GetProperty("outcome").GetProperty("xpAwarded").GetInt32());

        // All tests pass → challenge XP.
        var all = testIds.Select(id => new { testId = id, passed = true, message = (string?)null });
        var pass = await (await child.PostJson($"/api/challenges/{challengeId}/submissions", new { files, results = all })).Json();
        Assert.True(pass.GetProperty("passed").GetBoolean());
        Assert.Equal(10, pass.GetProperty("outcome").GetProperty("xpAwarded").GetInt32());
        Assert.False(pass.GetProperty("outcome").GetProperty("lessonCompleted").GetBoolean()); // quiz still to do

        // Passing again awards nothing more.
        var repeat = await (await child.PostJson($"/api/challenges/{challengeId}/submissions", new { files, results = all })).Json();
        Assert.Equal(0, repeat.GetProperty("outcome").GetProperty("xpAwarded").GetInt32());

        // Quiz: answer every question correctly. The API never sends which option is correct, so the
        // test reads the seed file, exactly as an author would check their own content.
        var quiz = lesson.GetProperty("quiz");
        var answers = CorrectAnswers(quiz);
        var attempt = await (await child.PostJson($"/api/quizzes/{quiz.GetProperty("id").GetInt32()}/attempts", new { answers })).Json();
        Assert.True(attempt.GetProperty("passed").GetBoolean());
        var outcome = attempt.GetProperty("outcome");
        Assert.True(outcome.GetProperty("lessonCompleted").GetBoolean());
        Assert.Equal(10, outcome.GetProperty("xpAwarded").GetInt32()); // 5 quiz + 5 lesson
        Assert.Equal(20, outcome.GetProperty("totalXp").GetInt32());
        Assert.Contains(outcome.GetProperty("newBadges").EnumerateArray(), b => b.GetProperty("code").GetString() == "first-steps");

        // The course isn't complete: lessons 2–6 are unpublished, so no course XP or certificate.
        Assert.False(outcome.GetProperty("courseCompleted").GetBoolean());

        // Parent dashboard reflects it.
        var progress = await parent.GetJson($"/api/parent/children/{childId}/progress");
        Assert.Equal(1, progress.GetProperty("child").GetProperty("lessonsCompleted").GetInt32());
        Assert.Equal(20, progress.GetProperty("child").GetProperty("xp").GetInt32());
        Assert.Equal(14, progress.GetProperty("last14Days").GetArrayLength());
    }

    [Fact]
    public async Task Hints_unlock_in_order_and_the_solution_is_a_separate_step_after_them()
    {
        var (parent, child, childId) = await FamilyWithConsentedExplorer("SwiftPuffin12");
        var lesson = await ExplorerLesson(child);
        var id = lesson.GetProperty("challenge").GetProperty("id").GetInt32();

        Assert.Equal(HttpStatusCode.Conflict, (await child.PostJson($"/api/challenges/{id}/hints/2")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await child.PostJson($"/api/challenges/{id}/solution")).StatusCode);

        // Hint 1 is a guiding question, written twice: for the screen and for a voice.
        var h1 = await (await child.PostJson($"/api/challenges/{id}/hints/1")).Json();
        Assert.EndsWith("?", h1.GetProperty("markdown").GetString()!.TrimEnd());
        Assert.EndsWith("?", h1.GetProperty("speechText").GetString()!.TrimEnd());
        Assert.False(string.IsNullOrEmpty(h1.GetProperty("mood").GetString()));
        Assert.False(h1.TryGetProperty("solutionFiles", out _));

        Assert.Equal(HttpStatusCode.OK, (await child.PostJson($"/api/challenges/{id}/hints/2")).StatusCode);
        var h3 = await (await child.PostJson($"/api/challenges/{id}/hints/3")).Json();
        Assert.False(h3.TryGetProperty("solutionFiles", out _)); // hint 3 is nearly the answer, never the answer
        Assert.Equal(JsonValueKind.Null, (await ExplorerLesson(child)).GetProperty("challenge").GetProperty("solutionFiles").ValueKind);

        // After all three hints the learner can ask for the solution, and it stays unlocked.
        var solution = await child.PostJson($"/api/challenges/{id}/solution");
        Assert.Equal(HttpStatusCode.OK, solution.StatusCode);
        Assert.Contains("<h1>Welcome to my page</h1>", (await solution.Json()).GetProperty("solutionFiles").GetProperty("index.html").GetString());
        var reloaded = (await ExplorerLesson(child)).GetProperty("challenge");
        Assert.Equal(3, reloaded.GetProperty("revealedHints").GetArrayLength());
        Assert.Equal(JsonValueKind.Object, reloaded.GetProperty("solutionFiles").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await child.PostJson($"/api/challenges/{id}/solution")).StatusCode); // idempotent

        // The parent still sees how many hints were used (the parent view lists courses the child has started).
        await child.PostJson($"/api/lessons/{lesson.GetProperty("id").GetInt32()}/viewed");
        var progress = await parent.GetJson($"/api/parent/children/{childId}/progress");
        var used = progress.GetProperty("courses").EnumerateArray().SelectMany(c => c.GetProperty("lessons").EnumerateArray())
            .Sum(l => l.GetProperty("hintsUsed").GetInt32());
        Assert.Equal(3, used);
    }

    [Fact]
    public async Task Daily_limit_locks_heartbeats_and_revoking_consent_blocks_sign_in()
    {
        var (parent, child, childId) = await FamilyWithConsentedExplorer("LuckyKoala13");

        var settings = await parent.PutJson($"/api/parent/children/{childId}/settings",
            new { dailyMinutesLimit = 5, weeklyGoalDays = 3, weeklyDigest = true, allowAiTutor = false, allowCommunity = false });
        Assert.Equal(HttpStatusCode.NoContent, settings.StatusCode);

        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        var beat = await child.PostJson("/api/activity/heartbeat", new { localDate = today });
        Assert.Equal(HttpStatusCode.OK, beat.StatusCode);
        Assert.Equal(30, (await beat.Json()).GetProperty("todaySeconds").GetInt32());

        // A second heartbeat inside 25 s doesn't add time.
        var fast = await (await child.PostJson("/api/activity/heartbeat", new { localDate = today })).Json();
        Assert.Equal(30, fast.GetProperty("todaySeconds").GetInt32());

        // AI tutor can't be switched on before it exists.
        var tutor = await parent.PutJson($"/api/parent/children/{childId}/settings",
            new { dailyMinutesLimit = 5, weeklyGoalDays = 3, weeklyDigest = true, allowAiTutor = true, allowCommunity = false });
        Assert.Equal(HttpStatusCode.BadRequest, tutor.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await parent.PostJson($"/api/parent/children/{childId}/consent/revoke")).StatusCode);
        var relogin = await _factory.NewClient().PostJson("/api/auth/login", new { login = "LuckyKoala13", password = "kid-password" });
        Assert.Equal(HttpStatusCode.Forbidden, relogin.StatusCode);
    }

    [Fact]
    public async Task Parents_cannot_see_other_families_children()
    {
        var (_, _, childId) = await FamilyWithConsentedExplorer("NimbleLynx14");
        var stranger = _factory.NewClient();
        await stranger.PostJson("/api/auth/register-parent", new { email = "stranger@example.com", password = "stranger-pass", confirmAdult = true });

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/parent/children/{childId}/progress")).StatusCode);
    }

    [Fact]
    public async Task Under_13_self_registration_is_refused_and_cannot_be_retried()
    {
        var client = _factory.NewClient();
        var young = await client.PostJson("/api/auth/register-teen",
            new { userName = "ZippyYak15", email = "zippy@example.com", password = "longenough", birthYear = ThisYear - 11, birthMonth = 1 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, young.StatusCode);

        var retry = await client.PostJson("/api/auth/register-teen",
            new { userName = "ZippyYak15", email = "zippy@example.com", password = "longenough", birthYear = ThisYear - 15, birthMonth = 1 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, retry.StatusCode);

        var teen = await _factory.NewClient().PostJson("/api/auth/register-teen",
            new { userName = "TurboTiger16", email = "turbo@example.com", password = "longenough", birthYear = ThisYear - 15, birthMonth = 1 });
        Assert.Equal(HttpStatusCode.OK, teen.StatusCode);
        Assert.Equal("Builder", (await teen.Json()).GetProperty("ageBand").GetString());
    }

    [Fact]
    public async Task Concurrent_requests_for_the_same_lesson_do_not_fail()
    {
        var (_, child, _) = await FamilyWithConsentedExplorer("QuickOwl17");
        var lesson = await ExplorerLesson(child);
        var id = lesson.GetProperty("id").GetInt32();
        var challengeId = lesson.GetProperty("challenge").GetProperty("id").GetInt32();
        var all = lesson.GetProperty("challenge").GetProperty("tests").EnumerateArray()
            .Select(t => new { testId = t.GetProperty("id").GetInt32(), passed = true, message = (string?)null }).ToList();
        var files = new Dictionary<string, string> { ["index.html"] = "<h1>Welcome to my page</h1><p>Otters hold hands.</p>" };

        // e.g. React dev mode double-firing effects, or a double-clicked "Check my code".
        var viewed = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => child.PostJson($"/api/lessons/{id}/viewed")));
        Assert.All(viewed, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));

        var submits = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            child.PostJson($"/api/challenges/{challengeId}/submissions", new { files, results = all })));
        Assert.All(submits, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        var xp = 0;
        foreach (var r in submits) xp += (await r.Json()).GetProperty("outcome").GetProperty("xpAwarded").GetInt32();
        Assert.Equal(10, xp); // awarded exactly once across the racing requests
    }

    [Fact]
    public async Task State_changing_requests_must_be_json()
    {
        var client = _factory.NewClient();
        var form = new StringContent("login=a&password=b", Encoding.UTF8, "application/x-www-form-urlencoded");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.PostAsync("/api/auth/login", form)).StatusCode);
    }

    [Fact]
    public async Task Anonymous_visitors_can_browse_but_not_open_lessons()
    {
        var client = _factory.NewClient();
        var tracks = await client.GetJson("/api/tracks");
        Assert.Equal("web-foundations", tracks[0].GetProperty("slug").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/lessons/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/does-not-exist")).StatusCode);
    }

    [Fact]
    public async Task Tracks_follow_the_curriculum_order_with_angular_after_typescript_and_databases_after_csharp()
    {
        var client = _factory.NewClient();
        var tracks = (await client.GetJson("/api/tracks")).EnumerateArray().Select(t => t.GetProperty("slug").GetString()).ToList();
        Assert.Equal(["web-foundations", "javascript", "typescript", "angular", "frontend-frameworks", "csharp", "databases", "web-apis", "capstone"], tracks);

        // Angular challenges are TypeScript projects: the API sends kind "Angular" with .ts starter files.
        var (_, child, _) = await FamilyWithConsentedExplorer("NgFox");
        var course = await child.GetJson("/api/courses/angular-basics");
        var first = course.GetProperty("lessons").EnumerateArray().First(l => l.GetProperty("slug").GetString() == "components-explorer");
        var lesson = await child.GetJson($"/api/lessons/{first.GetProperty("id").GetInt32()}");
        var challenge = lesson.GetProperty("challenge");
        Assert.Equal("Angular", challenge.GetProperty("kind").GetString());
        Assert.True(challenge.GetProperty("starterFiles").TryGetProperty("app.ts", out _));

        // C# challenges run in the browser's C# runner: kind "CSharp" with a Program.cs. Every lesson is published,
        // so the course can be completed (and earns its badge, see Every_published_course_has_a_completion_badge).
        var csharp = await child.GetJson("/api/courses/csharp-basics");
        var csLessons = csharp.GetProperty("lessons").EnumerateArray().ToList();
        var hello = csLessons.First(l => l.GetProperty("slug").GetString() == "hello-csharp-explorer");
        var csLesson = await child.GetJson($"/api/lessons/{hello.GetProperty("id").GetInt32()}");
        Assert.Equal("CSharp", csLesson.GetProperty("challenge").GetProperty("kind").GetString());
        Assert.True(csLesson.GetProperty("challenge").GetProperty("starterFiles").TryGetProperty("Program.cs", out _));
        Assert.All(csLessons, l => Assert.True(l.GetProperty("isPublished").GetBoolean()));

        // SQL challenges run in the browser's SQL runner: kind "Sql", the learner's query.sql, and the hidden
        // .setup.sql that builds the database (the editor never shows dotfiles).
        var sql = await child.GetJson("/api/courses/sql-basics");
        var sqlFirst = sql.GetProperty("lessons").EnumerateArray().First(l => l.GetProperty("slug").GetString() == "databases-explorer");
        var sqlChallenge = (await child.GetJson($"/api/lessons/{sqlFirst.GetProperty("id").GetInt32()}")).GetProperty("challenge");
        Assert.Equal("Sql", sqlChallenge.GetProperty("kind").GetString());
        Assert.True(sqlChallenge.GetProperty("starterFiles").TryGetProperty("query.sql", out _));
        Assert.Contains("CREATE TABLE critters", sqlChallenge.GetProperty("starterFiles").GetProperty(".setup.sql").GetString());
    }

    [Fact]
    public async Task Explorer_js_lessons_offer_blocks_without_leaking_the_blocks_solution()
    {
        var (_, child, _) = await FamilyWithConsentedExplorer("BlockFox");
        var course = await child.GetJson("/api/courses/javascript-basics");
        var lessons = course.GetProperty("lessons").EnumerateArray().ToList();
        int IdOf(string slug) => lessons.First(l => l.GetProperty("slug").GetString() == slug).GetProperty("id").GetInt32();

        var lesson = await child.GetJson($"/api/lessons/{IdOf("variables-explorer")}");
        var blocks = lesson.GetProperty("challenge").GetProperty("blocks");
        Assert.Contains("variables", blocks.GetProperty("toolbox").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(JsonValueKind.Object, blocks.GetProperty("starter").ValueKind);
        // The content file has a solution workspace for the content checks; it must never reach a learner.
        Assert.False(blocks.TryGetProperty("solution", out _));
        Assert.DoesNotContain("Zara", blocks.GetRawText());

        // Lessons without blocks send null.
        var dom = await child.GetJson($"/api/lessons/{IdOf("dom-explorer")}");
        Assert.Equal(JsonValueKind.Null, dom.GetProperty("challenge").GetProperty("blocks").ValueKind);
    }

    [Fact]
    public async Task Course_length_is_the_learners_own_band()
    {
        var (_, explorer, _) = await FamilyWithConsentedExplorer("TimeFox");
        var course = await explorer.GetJson("/api/courses/javascript-basics");
        var lessonMinutes = course.GetProperty("lessons").EnumerateArray().Sum(l => l.GetProperty("estimatedMinutes").GetInt32());
        Assert.Equal(lessonMinutes, course.GetProperty("estimatedMinutes").GetInt32());
        Assert.Equal(45, lessonMinutes); // nine 5-minute Explorer lessons, not the 120-minute Builder path

        var track = await explorer.GetJson("/api/tracks/javascript");
        Assert.Equal(45, track.GetProperty("courses")[0].GetProperty("estimatedMinutes").GetInt32());

        // Signed-out visitors see every band's lessons, so they get the course's authored estimate.
        var anon = _factory.NewClient();
        Assert.Equal(120, (await anon.GetJson("/api/courses/javascript-basics")).GetProperty("estimatedMinutes").GetInt32());
    }

    [Fact]
    public async Task Explorers_get_Juni_with_the_lessons_intro_recap_and_event_lines()
    {
        var (_, child, _) = await FamilyWithConsentedExplorer("ScriptOwl");
        var course = await child.GetJson("/api/courses/javascript-basics");
        var id = course.GetProperty("lessons").EnumerateArray()
            .First(l => l.GetProperty("slug").GetString() == "variables-explorer").GetProperty("id").GetInt32();

        var tutor = (await child.GetJson($"/api/lessons/{id}")).GetProperty("tutor");
        Assert.Equal("Juni", tutor.GetProperty("character").GetProperty("name").GetString());
        Assert.Contains("{mood}", tutor.GetProperty("character").GetProperty("portraitUrl").GetString());
        Assert.False(tutor.GetProperty("character").TryGetProperty("personaPrompt", out _)); // stays on the server

        var intro = tutor.GetProperty("intro").EnumerateArray().ToList();
        var recap = tutor.GetProperty("recap").EnumerateArray().ToList();
        Assert.NotEmpty(intro);
        Assert.NotEmpty(recap);
        // The caption can show code; the voice line says it in words.
        var recapDisplay = string.Join(" ", recap.Select(r => r.GetProperty("displayText").GetString()));
        var recapSpeech = string.Join(" ", recap.Select(r => r.GetProperty("speechText").GetString()));
        Assert.Contains("`console.log`", recapDisplay);
        Assert.Contains("console dot log", recapSpeech);
        Assert.Contains(recap, r => r.GetProperty("mood").GetString() is "Neutral" or "Encouraging" or "Thinking" or "Celebrating");

        var lines = tutor.GetProperty("lines");
        Assert.InRange(lines.GetProperty("pass").GetArrayLength(), 8, 10);
        Assert.True(lines.GetProperty("firstFail").GetArrayLength() >= 3);
        Assert.All(lines.GetProperty("stuckOffer").EnumerateArray(), l => Assert.EndsWith("?", l.GetProperty("speechText").GetString()));
    }

    [Fact]
    public async Task Builders_get_Rowan()
    {
        var teen = _factory.NewClient();
        var reg = await teen.PostJson("/api/auth/register-teen",
            new { userName = "RowanFan15", email = "rowanfan@example.com", password = "longenough", birthYear = ThisYear - 15, birthMonth = 1 });
        Assert.Equal(HttpStatusCode.OK, reg.StatusCode);

        var course = await teen.GetJson("/api/courses/html-basics");
        var id = course.GetProperty("lessons").EnumerateArray()
            .First(l => l.GetProperty("slug").GetString() == "tags-and-elements").GetProperty("id").GetInt32();
        var tutor = (await teen.GetJson($"/api/lessons/{id}")).GetProperty("tutor");
        Assert.Equal("Rowan", tutor.GetProperty("character").GetProperty("name").GetString());
        Assert.InRange(TutorRules.Sentences(string.Join(" ", tutor.GetProperty("intro").EnumerateArray().Select(l => l.GetProperty("speechText").GetString()))), 3, 4);
    }

    [Fact]
    public async Task Every_published_course_has_a_completion_badge()
    {
        var (_, child, _) = await FamilyWithConsentedExplorer("ShelfOwl");
        var codes = (await child.GetJson("/api/me/badges")).EnumerateArray().Select(b => b.GetProperty("code").GetString()).ToList();
        Assert.Contains("web-wanderer", codes);
        Assert.Contains("html-hero", codes);
        Assert.Contains("style-star", codes);
        Assert.Contains("js-juggler", codes);
        Assert.Contains("sharp-starter", codes);
        Assert.Contains("class-act", codes);
        Assert.Contains("data-detective", codes);
        Assert.Contains("table-linker", codes);
        Assert.Contains("route-ranger", codes);
        Assert.Contains("full-stack-finisher", codes);
    }

    /// <summary>Maps each quiz question to its correct option by matching prompts against the seed file.</summary>
    [Fact]
    public async Task A_capstone_milestone_starts_from_the_learners_own_passing_code()
    {
        var (_, child, _) = await FamilyWithConsentedExplorer("ClubFox12");
        var course = await child.GetJson("/api/courses/capstone-clubs");
        int Id(string slug) => course.GetProperty("lessons").EnumerateArray()
            .First(l => l.GetProperty("slug").GetString() == slug).GetProperty("id").GetInt32();

        // Before passing milestone 3: milestone 4 starts from the reference (its own starter).
        var m4 = (await child.GetJson($"/api/lessons/{Id("clubhub-signup-explorer")}")).GetProperty("challenge");
        Assert.Equal(JsonValueKind.Null, m4.GetProperty("startingFiles").ValueKind);
        Assert.Equal(JsonValueKind.Null, m4.GetProperty("carriedFrom").ValueKind);

        // A failing submission of milestone 3 doesn't count.
        var m3 = (await child.GetJson($"/api/lessons/{Id("clubhub-page-explorer")}")).GetProperty("challenge");
        var m3Id = m3.GetProperty("id").GetInt32();
        var testIds = m3.GetProperty("tests").EnumerateArray().Select(t => t.GetProperty("id").GetInt32()).ToList();
        const string mine = "// my own App.jsx\nexport default function App() { return null; }\n";
        var files = new Dictionary<string, string> { ["web/App.jsx"] = mine, ["web/index.html"] = "<div id=\"root\"></div>" };
        await child.PostJson($"/api/challenges/{m3Id}/submissions",
            new { files = new Dictionary<string, string>(files) { ["web/App.jsx"] = "// failing" },
                  results = testIds.Select(id => new { testId = id, passed = false, message = (string?)null }) });
        m4 = (await child.GetJson($"/api/lessons/{Id("clubhub-signup-explorer")}")).GetProperty("challenge");
        Assert.Equal(JsonValueKind.Null, m4.GetProperty("startingFiles").ValueKind);

        // After passing it: milestone 4 starts from their App.jsx; every other file is milestone 4's own.
        var pass = await child.PostJson($"/api/challenges/{m3Id}/submissions",
            new { files, results = testIds.Select(id => new { testId = id, passed = true, message = (string?)null }) });
        Assert.Equal(HttpStatusCode.OK, pass.StatusCode);
        m4 = (await child.GetJson($"/api/lessons/{Id("clubhub-signup-explorer")}")).GetProperty("challenge");
        var starting = m4.GetProperty("startingFiles");
        var starter = m4.GetProperty("starterFiles");
        Assert.Equal(mine, starting.GetProperty("web/App.jsx").GetString());
        Assert.NotEqual(mine, starter.GetProperty("web/App.jsx").GetString()); // the reference stays available
        Assert.Equal(starter.GetProperty("web/NewClubForm.jsx").GetString(), starting.GetProperty("web/NewClubForm.jsx").GetString());
        Assert.Equal(starter.GetProperty("api/Program.cs").GetString(), starting.GetProperty("api/Program.cs").GetString());
        Assert.Equal("Show your clubs on the page", m4.GetProperty("carriedFrom").GetString());
    }

    /// <summary>A real 2×1 PNG (signature, IHDR, IDAT, IEND), as the browser would send it.</summary>
    private const string TinyPng =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAYAAAD0In+KAAAAEUlEQVR4nGP4z8Dwn6GB4T8AD3sDfpRcrCkAAAAASUVORK5CYII=";

    [Fact]
    public async Task Passing_the_capstone_finale_puts_the_app_in_a_portfolio_only_the_family_can_see()
    {
        var (parent, child, childId) = await FamilyWithConsentedExplorer("OwlBuilder11");
        var course = await child.GetJson("/api/courses/capstone-clubs");
        int Id(string slug) => course.GetProperty("lessons").EnumerateArray()
            .First(l => l.GetProperty("slug").GetString() == slug).GetProperty("id").GetInt32();

        async Task<JsonElement> Pass(string slug, Dictionary<string, string> files)
        {
            var challenge = (await child.GetJson($"/api/lessons/{Id(slug)}")).GetProperty("challenge");
            var tests = challenge.GetProperty("tests").EnumerateArray().Select(t => t.GetProperty("id").GetInt32());
            var r = await child.PostJson($"/api/challenges/{challenge.GetProperty("id").GetInt32()}/submissions",
                new { files, results = tests.Select(id => new { testId = id, passed = true, message = (string?)null }) });
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            return (await r.Json()).GetProperty("outcome");
        }
        static Dictionary<string, string> App(string title, string marker) => new()
        {
            ["api/Program.cs"] = $"// {marker}\nvar app = WebApplication.Create();\napp.Run();\n",
            ["web/index.html"] = $"<!doctype html>\n<html><head><title>{title}</title></head><body><div id=\"root\"></div></body></html>",
            ["web/App.jsx"] = $"// {marker}\nexport default function App() {{ return null; }}\n",
        };

        // An earlier milestone isn't a portfolio project.
        var early = await Pass("clubhub-page-explorer", App("Not yet", "m3"));
        Assert.Equal(JsonValueKind.Null, early.GetProperty("portfolioEntryId").ValueKind);
        Assert.Equal(0, (await child.GetJson("/api/me/portfolio")).GetArrayLength());

        // The finale is: named after the page's <title>, under the course's theme, no picture yet.
        var outcome = await Pass("clubhub-yours-explorer", App("Owl &amp; Frog Club", "first"));
        var entryId = outcome.GetProperty("portfolioEntryId").GetString()!;
        var entry = Assert.Single((await child.GetJson("/api/me/portfolio")).EnumerateArray());
        Assert.Equal(entryId, entry.GetProperty("id").GetString());
        Assert.Equal("Owl & Frog Club", entry.GetProperty("title").GetString());
        Assert.Equal("Grove Club Hub", entry.GetProperty("theme").GetString());
        Assert.Equal("Make it yours", entry.GetProperty("lessonTitle").GetString());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("screenshotUrl").ValueKind);

        // The picture must be a real PNG.
        var svg = await child.PutJson($"/api/me/portfolio/{entryId}/screenshot", new { dataUrl = "data:image/png;base64,PHN2Zz48L3N2Zz4=" });
        Assert.Equal(HttpStatusCode.BadRequest, svg.StatusCode);
        var shot = await child.PutJson($"/api/me/portfolio/{entryId}/screenshot", new { dataUrl = TinyPng });
        Assert.Equal(HttpStatusCode.OK, shot.StatusCode);
        var shotUrl = (await shot.Json()).GetProperty("screenshotUrl").GetString()!;
        var image = await child.GetAsync(shotUrl);
        Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", image.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(Convert.FromBase64String(TinyPng.Split(',')[1]), await image.Content.ReadAsByteArrayAsync());

        // Download project gets the passing code.
        var files = await child.GetJson($"/api/portfolio/{entryId}/files");
        Assert.Contains("// first", files.GetProperty("files").GetProperty("web/App.jsx").GetString());

        // Renaming goes through the content filter.
        var leak = await child.PutJson($"/api/me/portfolio/{entryId}/title", new { title = "email me at kid@example.com" });
        Assert.Equal(HttpStatusCode.BadRequest, leak.StatusCode);
        var renamed = await child.PutJson($"/api/me/portfolio/{entryId}/title", new { title = "  The   Grove Clubs " });
        Assert.Equal("The Grove Clubs", (await renamed.Json()).GetProperty("title").GetString());

        // Passing again updates the same entry: their name stays, the code is the new code, the old picture goes.
        var again = await Pass("clubhub-yours-explorer", App("Something Else", "second"));
        Assert.Equal(entryId, again.GetProperty("portfolioEntryId").GetString());
        entry = Assert.Single((await child.GetJson("/api/me/portfolio")).EnumerateArray());
        Assert.Equal("The Grove Clubs", entry.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("screenshotUrl").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await child.GetAsync(shotUrl)).StatusCode);
        Assert.Contains("// second", (await child.GetJson($"/api/portfolio/{entryId}/files")).GetProperty("files").GetProperty("web/App.jsx").GetString());
        shotUrl = (await (await child.PutJson($"/api/me/portfolio/{entryId}/screenshot", new { dataUrl = TinyPng })).Json())
            .GetProperty("screenshotUrl").GetString()!;

        // Their parent sees it too.
        var parentView = Assert.Single((await parent.GetJson($"/api/parent/children/{childId}/portfolio")).EnumerateArray());
        Assert.Equal("The Grove Clubs", parentView.GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.OK, (await parent.GetAsync(shotUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await parent.GetAsync($"/api/portfolio/{entryId}/files")).StatusCode);

        // Nobody else does, and the answer doesn't say the entry exists.
        var stranger = _factory.NewClient();
        await stranger.PostJson("/api/auth/register-parent", new { email = "portfolio-stranger@example.com", password = "stranger-pass", confirmAdult = true });
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/parent/children/{childId}/portfolio")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(shotUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/portfolio/{entryId}/files")).StatusCode);
        var (_, otherKid, _) = await FamilyWithConsentedExplorer("PondNewt12");
        Assert.Equal(HttpStatusCode.NotFound, (await otherKid.GetAsync(shotUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherKid.PutJson($"/api/me/portfolio/{entryId}/title", new { title = "Mine now" })).StatusCode);
        Assert.Equal(0, (await otherKid.GetJson("/api/me/portfolio")).GetArrayLength());
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.NewClient().GetAsync(shotUrl)).StatusCode);

        // Deleting the account takes the portfolio with it.
        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/parent/children/{childId}") { Content = JsonContent.Create(new { }) };
        Assert.True((await parent.SendAsync(delete)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await parent.GetAsync(shotUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await parent.GetAsync($"/api/parent/children/{childId}/portfolio")).StatusCode);
    }

    private static Dictionary<string, int> CorrectAnswers(JsonElement quiz)
    {
        var seedPath = Path.Combine(AppContext.BaseDirectory, "Content", "courses", "html-basics.json");
        using var seed = JsonDocument.Parse(File.ReadAllText(seedPath));
        var seedQuestions = seed.RootElement.GetProperty("lessons").EnumerateArray()
            .Where(l => l.TryGetProperty("quiz", out _))
            .SelectMany(l => l.GetProperty("quiz").GetProperty("questions").EnumerateArray())
            .ToList();

        var answers = new Dictionary<string, int>();
        foreach (var q in quiz.GetProperty("questions").EnumerateArray())
        {
            var prompt = q.GetProperty("prompt").GetString();
            var seedQ = seedQuestions.First(s => s.GetProperty("prompt").GetString() == prompt);
            var correctText = seedQ.GetProperty("options").EnumerateArray().First(o => o.GetProperty("correct").GetBoolean()).GetProperty("text").GetString();
            var option = q.GetProperty("options").EnumerateArray().First(o => o.GetProperty("text").GetString() == correctText);
            answers[q.GetProperty("id").GetInt32().ToString()] = option.GetProperty("id").GetInt32();
        }
        return answers;
    }
}
