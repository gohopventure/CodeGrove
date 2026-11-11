using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Codegrove.Api.Data;
using Codegrove.Api.Data.Entities;
using Codegrove.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Codegrove.Api.Features.Learning;

// ---- Lesson payload ---------------------------------------------------------------------------

public sealed record TestSpecDto(int Id, string Label, string Kind, JsonElement Spec, string FailMessage);

public sealed record RevealedHintDto(int Level, string Markdown);

public sealed record ChallengeDto(
    int Id, ChallengeKind Kind, string InstructionsMarkdown, IReadOnlyDictionary<string, string> StarterFiles,
    IReadOnlyList<TestSpecDto> Tests, IReadOnlyList<int> HintLevels, IReadOnlyList<RevealedHintDto> RevealedHints,
    IReadOnlyDictionary<string, string>? SolutionFiles, int XpReward);

public sealed record QuizOptionDto(int Id, string Text);
public sealed record QuizQuestionDto(int Id, string Prompt, IReadOnlyList<QuizOptionDto> Options);
public sealed record QuizDto(int Id, int PassPercent, IReadOnlyList<QuizQuestionDto> Questions);

public sealed record LessonStateDto(bool LearnViewed, bool ChallengePassed, bool QuizPassed, bool Completed);

public sealed record LessonDto(
    int Id, string Slug, string Title, string CourseSlug, string CourseTitle, AgeBand AgeBand, int EstimatedMinutes,
    string LearnMarkdown, string? VideoUrl, string? CaptionsUrl, string ExampleMarkdown, string? ExampleCode,
    ChallengeDto? Challenge, QuizDto? Quiz, LessonStateDto Progress, int? PreviousLessonId, int? NextLessonId);

// ---- Requests / responses ---------------------------------------------------------------------

public sealed record SubmitChallengeRequest(
    [Required] Dictionary<string, string> Files,
    [Required, MaxLength(50)] List<TestResultInput> Results);

public sealed record SubmissionResponse(bool Passed, ProgressOutcome Outcome);

public sealed record LatestSubmissionResponse(IReadOnlyDictionary<string, string> Files, bool Passed, DateTime CreatedAt);

public sealed record HintResponse(int Level, string Markdown, IReadOnlyDictionary<string, string>? SolutionFiles);

public sealed record QuizAttemptRequest([Required] Dictionary<int, int> Answers);

public sealed record QuizAttemptResponse(int ScorePercent, bool Passed, int PassPercent, IReadOnlyList<QuestionResult> Results, ProgressOutcome Outcome);

public sealed record HeartbeatRequest(DateOnly LocalDate);

/// <summary>The lesson player's API: load a lesson, submit a challenge, reveal hints, take the quiz.</summary>
[ApiController]
[Route("api")]
[Authorize(Policy = Policies.ActiveLearner)]
public sealed class LearningController(AppDbContext db, ProgressService progress, ActivityService activity, TimeProvider clock) : ControllerBase
{
    /// <summary>Total size cap for submitted files. Lessons are small; this stops abuse, not learning.</summary>
    public const int MaxSubmissionChars = 100_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [HttpGet("lessons/{id:int}")]
    public async Task<ActionResult<LessonDto>> Lesson(int id, CancellationToken ct)
    {
        var userId = User.UserId();
        var band = User.Band();

        var lesson = await db.Lessons.AsNoTracking()
            .Include(l => l.Course)
            .Include(l => l.Challenge!).ThenInclude(c => c.TestCases)
            .Include(l => l.Challenge!).ThenInclude(c => c.Hints)
            .Include(l => l.Quiz!).ThenInclude(q => q.Questions).ThenInclude(q => q.Options)
            .AsSplitQuery()
            .SingleOrDefaultAsync(l => l.Id == id && l.IsPublished && l.Course.IsPublished, ct);
        if (lesson is null) return NotFound();

        var state = await db.LessonProgress.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId && p.LessonId == id, ct);

        ChallengeDto? challenge = null;
        if (lesson.Challenge is { } c)
        {
            var revealedIds = await db.HintReveals.Where(r => r.UserId == userId && r.Hint.ChallengeId == c.Id)
                .Select(r => r.HintId).ToListAsync(ct);
            var revealed = c.Hints.Where(h => revealedIds.Contains(h.Id)).OrderBy(h => h.Level).ToList();
            var solutionUnlocked = revealed.Any(h => h.Level == 3) || state?.ChallengePassedAt != null;

            challenge = new ChallengeDto(c.Id, c.Kind, c.InstructionsMarkdown, Files(c.StarterFilesJson),
                c.TestCases.OrderBy(t => t.SortOrder)
                    .Select(t => new TestSpecDto(t.Id, t.Label, t.Kind, JsonDocument.Parse(t.SpecJson).RootElement, t.FailMessage))
                    .ToList(),
                c.Hints.Select(h => h.Level).OrderBy(l => l).ToList(),
                revealed.Select(h => new RevealedHintDto(h.Level, h.Markdown)).ToList(),
                solutionUnlocked ? Files(c.SolutionFilesJson) : null,
                c.XpReward);
        }

        QuizDto? quiz = lesson.Quiz is { } q
            ? new QuizDto(q.Id, q.PassPercent, q.Questions.OrderBy(x => x.SortOrder)
                .Select(x => new QuizQuestionDto(x.Id, x.Prompt,
                    x.Options.OrderBy(o => o.SortOrder).Select(o => new QuizOptionDto(o.Id, o.Text)).ToList()))
                .ToList())
            : null;

        // Previous/next within the course, for this learner's band, published only.
        var siblings = await db.Lessons.AsNoTracking()
            .Where(l => l.CourseId == lesson.CourseId && l.IsPublished && (l.AgeBand == AgeBand.Both || l.AgeBand == band))
            .OrderBy(l => l.SortOrder).Select(l => l.Id).ToListAsync(ct);
        var index = siblings.IndexOf(lesson.Id);

        return new LessonDto(lesson.Id, lesson.Slug, lesson.Title, lesson.Course.Slug, lesson.Course.Title, lesson.AgeBand,
            lesson.EstimatedMinutes, lesson.LearnMarkdown, lesson.VideoUrl, lesson.CaptionsUrl, lesson.ExampleMarkdown,
            lesson.ExampleCode, challenge, quiz,
            new LessonStateDto(state?.LearnViewedAt != null, state?.ChallengePassedAt != null,
                state?.QuizPassedAt != null, state?.CompletedAt != null),
            index > 0 ? siblings[index - 1] : null,
            index >= 0 && index < siblings.Count - 1 ? siblings[index + 1] : null);
    }

    [HttpPost("lessons/{id:int}/viewed")]
    public async Task<IActionResult> Viewed(int id, CancellationToken ct)
    {
        if (!await db.Lessons.AnyAsync(l => l.Id == id && l.IsPublished, ct)) return NotFound();
        await progress.MarkLearnViewedAsync(User.UserId(), id, ct);
        return NoContent();
    }

    [HttpPost("challenges/{id:int}/submissions")]
    public async Task<ActionResult<SubmissionResponse>> Submit(int id, SubmitChallengeRequest req, CancellationToken ct)
    {
        if (req.Files.Sum(f => f.Key.Length + f.Value.Length) > MaxSubmissionChars)
            return Problem(statusCode: 413, title: "That's a lot of code! Try trimming it down a bit.");

        var challenge = await db.Challenges.Include(c => c.TestCases)
            .SingleOrDefaultAsync(c => c.Id == id && c.Lesson.IsPublished, ct);
        if (challenge is null) return NotFound();

        var (submission, outcome) = await progress.RecordSubmissionAsync(
            User.UserId(), User.Band(), challenge, req.Files, req.Results, ct);
        return new SubmissionResponse(submission.Passed, outcome);
    }

    [HttpGet("challenges/{id:int}/submissions/latest")]
    public async Task<ActionResult<LatestSubmissionResponse>> Latest(int id, CancellationToken ct)
    {
        var userId = User.UserId();
        var latest = await db.Submissions.AsNoTracking()
            .Where(s => s.UserId == userId && s.ChallengeId == id)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);
        return latest is null ? NoContent() : new LatestSubmissionResponse(Files(latest.FilesJson), latest.Passed, latest.CreatedAt);
    }

    /// <summary>
    /// Reveals the next hint. Hints unlock in order (a nudge, then more help, then the solution)
    /// and cost no XP; parents and teachers can see how many were used.
    /// </summary>
    [HttpPost("challenges/{id:int}/hints/{level:int}")]
    public async Task<ActionResult<HintResponse>> RevealHint(int id, int level, CancellationToken ct)
    {
        var userId = User.UserId();
        var hints = await db.Hints.Where(h => h.ChallengeId == id).OrderBy(h => h.Level).ToListAsync(ct);
        var hint = hints.SingleOrDefault(h => h.Level == level);
        if (hint is null) return NotFound();

        var revealed = await db.HintReveals.Where(r => r.UserId == userId && r.Hint.ChallengeId == id)
            .Select(r => r.Hint.Level).ToListAsync(ct);
        var earlierLevels = hints.Where(h => h.Level < level).Select(h => h.Level);
        if (earlierLevels.Any(l => !revealed.Contains(l)))
            return Problem(statusCode: 409, title: "Open the earlier hints first.");

        if (!revealed.Contains(level))
        {
            db.HintReveals.Add(new HintReveal { UserId = userId, HintId = hint.Id, RevealedAt = clock.GetUtcNow().UtcDateTime });
            await db.SaveChangesAsync(ct);
        }

        IReadOnlyDictionary<string, string>? solution = null;
        if (level == hints.Max(h => h.Level))
        {
            var json = await db.Challenges.Where(c => c.Id == id).Select(c => c.SolutionFilesJson).SingleAsync(ct);
            solution = Files(json);
        }

        return new HintResponse(hint.Level, hint.Markdown, solution);
    }

    [HttpPost("quizzes/{id:int}/attempts")]
    public async Task<ActionResult<QuizAttemptResponse>> Attempt(int id, QuizAttemptRequest req, CancellationToken ct)
    {
        var quiz = await db.Quizzes
            .Include(q => q.Questions).ThenInclude(q => q.Options)
            .SingleOrDefaultAsync(q => q.Id == id && q.Lesson.IsPublished, ct);
        if (quiz is null) return NotFound();

        var (grade, outcome) = await progress.RecordQuizAttemptAsync(User.UserId(), User.Band(), quiz, req.Answers, ct);
        return new QuizAttemptResponse(grade.ScorePercent, grade.Passed, quiz.PassPercent, grade.Results, outcome);
    }

    /// <summary>
    /// Sent every 30 seconds while a lesson tab is visible. 423 Locked means the parent's daily
    /// limit is reached; the player then shows a friendly "see you tomorrow" screen.
    /// </summary>
    [HttpPost("activity/heartbeat")]
    public async Task<ActionResult<HeartbeatResult>> Heartbeat(HeartbeatRequest req, CancellationToken ct)
    {
        var result = await activity.HeartbeatAsync(User.UserId(), req.LocalDate, ct);
        return result.LimitReached ? StatusCode(StatusCodes.Status423Locked, result) : result;
    }

    private static IReadOnlyDictionary<string, string> Files(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json, Json) ?? [];
}
