using System.ComponentModel.DataAnnotations;
using Codegrove.Api.Data;
using Codegrove.Api.Data.Entities;
using Codegrove.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Codegrove.Api.Features.Parent;

public sealed record CreateChildRequest(
    [Required, MaxLength(20)] string UserName,
    [Required, MinLength(8), MaxLength(128)] string Password,
    int BirthYear,
    int BirthMonth,
    AgeBand? AgeBand);

public sealed record ChildSummaryDto(
    Guid Id, string UserName, AgeBand AgeBand, AccountStatus Status, ConsentStatus? ConsentStatus,
    int Xp, int Level, int LessonsCompleted, int MinutesLast7Days, DateTime? LastActiveAt);

public sealed record CreateChildResponse(ChildSummaryDto Child, bool ConsentRequired, string? DevConfirmUrl);

public sealed record ChildSettingsDto(
    [Range(5, 600)] int? DailyMinutesLimit,
    [Range(1, 7)] int WeeklyGoalDays,
    bool WeeklyDigest,
    bool AllowAiTutor,
    bool AllowCommunity);

public sealed record LessonProgressDto(
    int LessonId, string Title, bool ChallengePassed, int? BestQuizScore, int HintsUsed, DateTime? CompletedAt);

public sealed record CourseProgressDto(string Slug, string Title, int LessonsTotal, int LessonsCompleted, IReadOnlyList<LessonProgressDto> Lessons);

public sealed record DailyMinutesDto(DateOnly Date, int Minutes, int LessonsCompleted);

public sealed record ChildProgressResponse(
    ChildSummaryDto Child,
    ChildSettingsDto Settings,
    IReadOnlyList<CourseProgressDto> Courses,
    IReadOnlyList<DailyMinutesDto> Last14Days,
    IReadOnlyList<string> Badges);

public sealed record ResetPasswordRequest([Required, MinLength(8), MaxLength(128)] string NewPassword);

/// <summary>Everything a parent can see and do for their linked children.</summary>
[ApiController]
[Route("api/parent")]
[Authorize(Policy = Policies.Parent)]
public sealed class ParentController(
    AppDbContext db,
    UserManager<AppUser> users,
    ConsentService consent,
    ProgressService progress,
    IWebHostEnvironment env,
    TimeProvider clock) : ControllerBase
{
    [HttpGet("children")]
    public async Task<IReadOnlyList<ChildSummaryDto>> Children(CancellationToken ct)
    {
        var parentId = User.UserId();
        var childIds = await db.ParentChildLinks.Where(l => l.ParentUserId == parentId).Select(l => l.ChildUserId).ToListAsync(ct);

        var result = new List<ChildSummaryDto>();
        foreach (var id in childIds) result.Add(await Summary(id, ct));
        return result.OrderBy(c => c.UserName).ToList();
    }

    [HttpGet("username-suggestions")]
    public IReadOnlyList<string> UsernameSuggestions() =>
        Enumerable.Range(0, 5).Select(_ => UsernameGenerator.Next()).ToList();

    [HttpPost("children")]
    public async Task<ActionResult<CreateChildResponse>> CreateChild(CreateChildRequest req, CancellationToken ct)
    {
        var parent = await users.GetUserAsync(User);
        if (parent is null) return Unauthorized();

        var result = await consent.CreateChildAsync(parent,
            new CreateChildCommand(req.UserName, req.Password, req.BirthYear, req.BirthMonth, req.AgeBand), ct);

        if (!result.Succeeded)
            return ValidationProblem(new ValidationProblemDetails(result.Errors.ToDictionary(kv => kv.Key, kv => kv.Value)));

        // The confirm link is only echoed back in Development so you can test without an inbox.
        var devUrl = env.IsDevelopment() ? result.ConfirmUrl : null;
        return Created($"/api/parent/children/{result.Child!.Id}",
            new CreateChildResponse(await Summary(result.Child.Id, ct), result.ConsentRequired, devUrl));
    }

    [HttpGet("children/{childId:guid}/progress")]
    public async Task<ActionResult<ChildProgressResponse>> ChildProgress(Guid childId, [FromQuery] DateOnly? localDate, CancellationToken ct)
    {
        if (!await consent.IsParentOfAsync(User.UserId(), childId, ct)) return NotFound();

        var child = await db.Users.AsNoTracking().SingleAsync(u => u.Id == childId, ct);
        var settings = await db.LearnerSettings.AsNoTracking().SingleOrDefaultAsync(s => s.UserId == childId, ct)
                       ?? new LearnerSettings { UserId = childId };

        var today = localDate ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var from = today.AddDays(-13);
        var activity = await db.DailyActivity.AsNoTracking()
            .Where(d => d.UserId == childId && d.Date >= from && d.Date <= today)
            .ToDictionaryAsync(d => d.Date, ct);
        var last14 = Enumerable.Range(0, 14)
            .Select(i => from.AddDays(i))
            .Select(d => activity.TryGetValue(d, out var a)
                ? new DailyMinutesDto(d, (int)Math.Round(a.ActiveSeconds / 60.0), a.LessonsCompleted)
                : new DailyMinutesDto(d, 0, 0))
            .ToList();

        var badges = await db.UserBadges.AsNoTracking().Where(b => b.UserId == childId)
            .OrderBy(b => b.AwardedAt).Select(b => b.Badge.Name).ToListAsync(ct);

        return new ChildProgressResponse(
            await Summary(childId, ct),
            new ChildSettingsDto(settings.DailyMinutesLimit, settings.WeeklyGoalDays, settings.WeeklyDigest,
                settings.AllowAiTutor, settings.AllowCommunity),
            await CourseProgress(childId, child.AgeBand, ct),
            last14,
            badges);
    }

    [HttpPut("children/{childId:guid}/settings")]
    public async Task<IActionResult> UpdateSettings(Guid childId, ChildSettingsDto req, CancellationToken ct)
    {
        if (!await consent.IsParentOfAsync(User.UserId(), childId, ct)) return NotFound();

        // These features don't exist yet. When they do, turning them on needs extra steps
        // (third-party consent for the tutor), so the API refuses rather than silently storing true.
        if (req.AllowAiTutor || req.AllowCommunity)
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["general"] = ["The AI tutor and community features aren't available yet."],
            }));

        var settings = await db.LearnerSettings.FindAsync([childId], ct);
        if (settings is null)
        {
            settings = new LearnerSettings { UserId = childId };
            db.LearnerSettings.Add(settings);
        }

        settings.DailyMinutesLimit = req.DailyMinutesLimit;
        settings.WeeklyGoalDays = req.WeeklyGoalDays;
        settings.WeeklyDigest = req.WeeklyDigest;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("children/{childId:guid}/password")]
    public async Task<IActionResult> ResetPassword(Guid childId, ResetPasswordRequest req, CancellationToken ct)
    {
        if (!await consent.IsParentOfAsync(User.UserId(), childId, ct)) return NotFound();

        var child = await users.FindByIdAsync(childId.ToString());
        if (child is null) return NotFound();

        var token = await users.GeneratePasswordResetTokenAsync(child);
        var result = await users.ResetPasswordAsync(child, token, req.NewPassword);
        if (!result.Succeeded)
            return ValidationProblem(new ValidationProblemDetails(ConsentService.IdentityErrors(result)));

        await users.SetLockoutEndDateAsync(child, null); // a locked-out kid shouldn't have to wait after a reset
        return NoContent();
    }

    [HttpPost("children/{childId:guid}/consent/revoke")]
    public async Task<IActionResult> RevokeConsent(Guid childId, CancellationToken ct)
    {
        if (!await consent.IsParentOfAsync(User.UserId(), childId, ct)) return NotFound();
        await consent.RevokeAsync(User.UserId(), childId, ct);
        return NoContent();
    }

    [HttpDelete("children/{childId:guid}")]
    public async Task<IActionResult> DeleteChild(Guid childId, CancellationToken ct)
    {
        if (!await consent.IsParentOfAsync(User.UserId(), childId, ct)) return NotFound();
        await consent.DeleteChildAsync(User.UserId(), childId, ct);
        return NoContent();
    }

    // ----------------------------------------------------------------------------------------

    private async Task<ChildSummaryDto> Summary(Guid childId, CancellationToken ct)
    {
        var child = await db.Users.AsNoTracking().SingleAsync(u => u.Id == childId, ct);
        var consentStatus = await db.ParentalConsents.AsNoTracking()
            .Where(c => c.ChildUserId == childId)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => (ConsentStatus?)c.Status)
            .FirstOrDefaultAsync(ct);

        var xp = await progress.TotalXpAsync(childId, ct);
        var lessons = await db.LessonProgress.CountAsync(p => p.UserId == childId && p.CompletedAt != null, ct);
        var weekAgo = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddDays(-6);
        var seconds = await db.DailyActivity
            .Where(d => d.UserId == childId && d.Date >= weekAgo)
            .SumAsync(d => (int?)d.ActiveSeconds, ct) ?? 0;

        return new ChildSummaryDto(child.Id, child.UserName!, child.AgeBand, child.Status, consentStatus,
            xp, Levels.LevelFor(xp), lessons, (int)Math.Round(seconds / 60.0), child.LastActiveAt);
    }

    /// <summary>Per-course detail for every course the child has started.</summary>
    private async Task<IReadOnlyList<CourseProgressDto>> CourseProgress(Guid childId, AgeBand band, CancellationToken ct)
    {
        var startedCourseIds = await db.LessonProgress.AsNoTracking()
            .Where(p => p.UserId == childId)
            .Select(p => p.Lesson.CourseId)
            .Distinct()
            .ToListAsync(ct);

        var lessons = await db.Lessons.AsNoTracking()
            .Where(l => startedCourseIds.Contains(l.CourseId) && l.IsPublished
                        && (l.AgeBand == AgeBand.Both || l.AgeBand == band))
            .Select(l => new
            {
                l.Id, l.Title, l.SortOrder, l.CourseId, CourseSlug = l.Course.Slug, CourseTitle = l.Course.Title,
                ChallengeId = l.Challenge != null ? (int?)l.Challenge.Id : null,
                QuizId = l.Quiz != null ? (int?)l.Quiz.Id : null,
            })
            .ToListAsync(ct);

        var lessonIds = lessons.Select(l => l.Id).ToList();
        var progressRows = await db.LessonProgress.AsNoTracking()
            .Where(p => p.UserId == childId && lessonIds.Contains(p.LessonId))
            .ToDictionaryAsync(p => p.LessonId, ct);

        var quizIds = lessons.Where(l => l.QuizId != null).Select(l => l.QuizId!.Value).ToList();
        var bestScores = await db.QuizAttempts.AsNoTracking()
            .Where(a => a.UserId == childId && quizIds.Contains(a.QuizId))
            .GroupBy(a => a.QuizId)
            .Select(g => new { QuizId = g.Key, Best = g.Max(a => a.ScorePercent) })
            .ToDictionaryAsync(x => x.QuizId, x => x.Best, ct);

        var hintCounts = await db.HintReveals.AsNoTracking()
            .Where(h => h.UserId == childId)
            .GroupBy(h => h.Hint.ChallengeId)
            .Select(g => new { ChallengeId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ChallengeId, x => x.Count, ct);

        return lessons
            .GroupBy(l => new { l.CourseId, l.CourseSlug, l.CourseTitle })
            .Select(g =>
            {
                var items = g.OrderBy(l => l.SortOrder).Select(l =>
                {
                    progressRows.TryGetValue(l.Id, out var p);
                    int? best = l.QuizId is int q && bestScores.TryGetValue(q, out var s) ? s : null;
                    var hints = l.ChallengeId is int c && hintCounts.TryGetValue(c, out var h) ? h : 0;
                    return new LessonProgressDto(l.Id, l.Title, p?.ChallengePassedAt != null, best, hints, p?.CompletedAt);
                }).ToList();
                return new CourseProgressDto(g.Key.CourseSlug, g.Key.CourseTitle, items.Count,
                    items.Count(i => i.CompletedAt != null), items);
            })
            .OrderBy(c => c.Title)
            .ToList();
    }
}
