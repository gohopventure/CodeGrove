using Codegrove.Api.Data;
using Codegrove.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Codegrove.Api.Services;

public sealed record HeartbeatResult(int TodaySeconds, int? DailyMinutesLimit, bool LimitReached);

/// <summary>
/// Tracks active learning time from lesson-player heartbeats and enforces the parent's daily limit.
/// </summary>
public sealed class ActivityService(AppDbContext db, TimeProvider clock, IMemoryCache cache)
{
    public const int HeartbeatSeconds = 30;

    /// <param name="localDate">The learner's local date, so "today" matches their day, not UTC's.</param>
    public async Task<HeartbeatResult> HeartbeatAsync(Guid userId, DateOnly localDate, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var date = ClampToPlausibleLocalDate(localDate, now);

        var limit = await db.LearnerSettings
            .Where(s => s.UserId == userId)
            .Select(s => s.DailyMinutesLimit)
            .SingleOrDefaultAsync(ct);

        var row = await db.DailyActivity.FindAsync([userId, date], ct);
        if (row is null)
        {
            row = new DailyActivity { UserId = userId, Date = date };
            db.DailyActivity.Add(row);
        }

        if (IsOverLimit(row.ActiveSeconds, limit))
            return new HeartbeatResult(row.ActiveSeconds, limit, true);

        // A client can't speed up the clock: at most one heartbeat counts per 25 seconds per learner.
        var key = $"hb:{userId}";
        if (!cache.TryGetValue(key, out _))
        {
            cache.Set(key, true, TimeSpan.FromSeconds(HeartbeatSeconds - 5));
            row.ActiveSeconds += HeartbeatSeconds;
            await db.Users.Where(u => u.Id == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastActiveAt, now), ct);
            await db.SaveChangesAsync(ct);
        }

        return new HeartbeatResult(row.ActiveSeconds, limit, IsOverLimit(row.ActiveSeconds, limit));
    }

    public async Task<int> SecondsTodayAsync(Guid userId, DateOnly localDate, CancellationToken ct = default)
    {
        var date = ClampToPlausibleLocalDate(localDate, clock.GetUtcNow().UtcDateTime);
        return await db.DailyActivity
            .Where(d => d.UserId == userId && d.Date == date)
            .Select(d => (int?)d.ActiveSeconds)
            .SingleOrDefaultAsync(ct) ?? 0;
    }

    private static bool IsOverLimit(int seconds, int? limitMinutes) =>
        limitMinutes is not null && seconds >= limitMinutes.Value * 60;

    /// <summary>Every time zone is within a day of UTC, so anything further out is ignored.</summary>
    private static DateOnly ClampToPlausibleLocalDate(DateOnly localDate, DateTime utcNow)
    {
        var utcToday = DateOnly.FromDateTime(utcNow);
        return Math.Abs(localDate.DayNumber - utcToday.DayNumber) <= 1 ? localDate : utcToday;
    }
}
