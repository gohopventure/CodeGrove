using Codegrove.Api.Data;
using Codegrove.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Codegrove.Api.Features.Me;

public sealed record BadgeDto(string Code, string Name, string Description, string IconUrl, bool Earned, DateTime? AwardedAt);

public sealed record CertificateSummaryDto(string Code, string TrackTitle, DateTime IssuedAt);

public sealed record ContinueLessonDto(int LessonId, string LessonTitle, string CourseTitle, string CourseSlug);

public sealed record DashboardResponse(
    string UserName,
    AgeBand AgeBand,
    int Xp,
    int Level,
    int XpForNextLevel,
    int StreakWeeks,
    int WeeklyGoalDays,
    int DaysActiveThisWeek,
    int TodaySeconds,
    int? DailyMinutesLimit,
    int LessonsCompleted,
    IReadOnlyList<BadgeDto> Badges,
    IReadOnlyList<CertificateSummaryDto> Certificates,
    ContinueLessonDto? Continue);

public sealed record PreferencesRequest(bool UseDyslexiaFont, bool ReducedMotion);

[ApiController]
[Route("api/me")]
[Authorize]
public sealed class MeController(AppDbContext db, ProgressService progress, ActivityService activity, TimeProvider clock) : ControllerBase
{
    [HttpGet("dashboard")]
    [Authorize(Policy = Policies.ActiveLearner)]
    public async Task<ActionResult<DashboardResponse>> Dashboard([FromQuery] DateOnly? localDate, CancellationToken ct)
    {
        var userId = User.UserId();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, ct);
        var settings = await db.LearnerSettings.AsNoTracking().SingleOrDefaultAsync(s => s.UserId == userId, ct);
        var today = localDate ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        var xp = await progress.TotalXpAsync(userId, ct);
        var level = Levels.LevelFor(xp);

        var since = today.AddDays(-7 * 26);
        var days = await db.DailyActivity.AsNoTracking()
            .Where(d => d.UserId == userId && d.Date >= since)
            .Select(d => new { d.Date, d.ActiveSeconds })
            .ToListAsync(ct);
        var streak = Streaks.Calculate(days.Select(d => (d.Date, d.ActiveSeconds)), settings?.WeeklyGoalDays ?? 3, today);

        var lessonsCompleted = await db.LessonProgress.CountAsync(p => p.UserId == userId && p.CompletedAt != null, ct);

        return new DashboardResponse(
            user.UserName!, user.AgeBand, xp, level, Levels.XpRequiredFor(level + 1),
            streak.StreakWeeks, streak.WeeklyGoalDays, streak.DaysActiveThisWeek,
            await activity.SecondsTodayAsync(userId, today, ct), settings?.DailyMinutesLimit,
            lessonsCompleted,
            await BadgesFor(userId, earnedOnly: true, ct),
            await CertificatesFor(userId, ct),
            await NextLesson(userId, user.AgeBand, ct));
    }

    /// <summary>The full badge shelf: earned badges plus the ones still to earn.</summary>
    [HttpGet("badges")]
    [Authorize(Policy = Policies.ActiveLearner)]
    public async Task<IReadOnlyList<BadgeDto>> Badges(CancellationToken ct) =>
        await BadgesFor(User.UserId(), earnedOnly: false, ct);

    [HttpPut("preferences")]
    public async Task<IActionResult> Preferences(PreferencesRequest req, CancellationToken ct)
    {
        var userId = User.UserId();
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.UseDyslexiaFont, req.UseDyslexiaFont)
            .SetProperty(u => u.ReducedMotion, req.ReducedMotion), ct);
        return NoContent();
    }

    private async Task<IReadOnlyList<BadgeDto>> BadgesFor(Guid userId, bool earnedOnly, CancellationToken ct)
    {
        var earned = await db.UserBadges.AsNoTracking()
            .Where(b => b.UserId == userId)
            .ToDictionaryAsync(b => b.BadgeId, b => b.AwardedAt, ct);

        var all = await db.Badges.AsNoTracking().OrderBy(b => b.Id).ToListAsync(ct);
        return all
            .Where(b => !earnedOnly || earned.ContainsKey(b.Id))
            .Select(b => new BadgeDto(b.Code, b.Name, b.Description, b.IconUrl,
                earned.ContainsKey(b.Id), earned.TryGetValue(b.Id, out var at) ? at : null))
            .ToList();
    }

    private async Task<IReadOnlyList<CertificateSummaryDto>> CertificatesFor(Guid userId, CancellationToken ct) =>
        await db.Certificates.AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.IssuedAt)
            .Select(c => new CertificateSummaryDto(c.VerificationCode, c.Track.Title, c.IssuedAt))
            .ToListAsync(ct);

    /// <summary>
    /// "Continue where you left off": the first unfinished published lesson in the course the learner
    /// touched most recently, or the very first lesson for their band if they haven't started.
    /// </summary>
    private async Task<ContinueLessonDto?> NextLesson(Guid userId, AgeBand band, CancellationToken ct)
    {
        var lastCourseId = await db.LessonProgress.AsNoTracking()
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.CompletedAt ?? p.QuizPassedAt ?? p.ChallengePassedAt ?? p.LearnViewedAt)
            .Select(p => (int?)p.Lesson.CourseId)
            .FirstOrDefaultAsync(ct);

        var completed = db.LessonProgress.Where(p => p.UserId == userId && p.CompletedAt != null).Select(p => p.LessonId);

        var query = db.Lessons.AsNoTracking()
            .Where(l => l.IsPublished && l.Course.IsPublished && (l.AgeBand == AgeBand.Both || l.AgeBand == band))
            .Where(l => !completed.Contains(l.Id));

        if (lastCourseId is not null) query = query.Where(l => l.CourseId == lastCourseId);

        return await query
            .OrderBy(l => l.Course.Tracks.Min(t => t.Track.SortOrder))
            .ThenBy(l => l.Course.Tracks.Min(t => t.SortOrder))
            .ThenBy(l => l.SortOrder)
            .Select(l => new ContinueLessonDto(l.Id, l.Title, l.Course.Title, l.Course.Slug))
            .FirstOrDefaultAsync(ct);
    }
}
