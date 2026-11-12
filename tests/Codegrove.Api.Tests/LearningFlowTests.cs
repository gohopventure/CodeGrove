using System.Net;
using System.Text;
using System.Text.Json;

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
    public async Task Hints_unlock_in_order_and_the_last_one_includes_the_solution()
    {
        var (_, child, _) = await FamilyWithConsentedExplorer("SwiftPuffin12");
        var lesson = await ExplorerLesson(child);
        var id = lesson.GetProperty("challenge").GetProperty("id").GetInt32();

        Assert.Equal(HttpStatusCode.Conflict, (await child.PostJson($"/api/challenges/{id}/hints/2")).StatusCode);

        var h1 = await (await child.PostJson($"/api/challenges/{id}/hints/1")).Json();
        Assert.Equal(JsonValueKind.Null, h1.GetProperty("solutionFiles").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await child.PostJson($"/api/challenges/{id}/hints/2")).StatusCode);

        var h3 = await (await child.PostJson($"/api/challenges/{id}/hints/3")).Json();
        Assert.Contains("<h1>Welcome to my page</h1>", h3.GetProperty("solutionFiles").GetProperty("index.html").GetString());
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
    public async Task Every_published_course_has_a_completion_badge()
    {
        var (_, child, _) = await FamilyWithConsentedExplorer("ShelfOwl");
        var codes = (await child.GetJson("/api/me/badges")).EnumerateArray().Select(b => b.GetProperty("code").GetString()).ToList();
        Assert.Contains("web-wanderer", codes);
        Assert.Contains("html-hero", codes);
        Assert.Contains("style-star", codes);
        Assert.Contains("js-juggler", codes);
    }

    /// <summary>Maps each quiz question to its correct option by matching prompts against the seed file.</summary>
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
