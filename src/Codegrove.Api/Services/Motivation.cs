namespace Codegrove.Api.Services;

/// <summary>XP levels: each level needs 50 XP more than the last (0, 50, 150, 300, 500, ...).</summary>
public static class Levels
{
    public static int XpRequiredFor(int level) => 50 * level * (level - 1) / 2;

    public static int LevelFor(int xp)
    {
        var level = 1;
        while (XpRequiredFor(level + 1) <= xp) level++;
        return level;
    }
}

public sealed record StreakInfo(int StreakWeeks, int DaysActiveThisWeek, int WeeklyGoalDays);

/// <summary>
/// Weekly-goal streaks: a week (Monday–Sunday) counts when the learner was active on at least
/// <c>goalDays</c> days. The current week never breaks a streak while it's still in progress,
/// so missing a day can't "lose" anything. This is deliberate: no streak guilt.
/// </summary>
public static class Streaks
{
    /// <summary>A day counts as active after 2 minutes of lesson time.</summary>
    public const int ActiveDayThresholdSeconds = 120;

    public static StreakInfo Calculate(IEnumerable<(DateOnly Date, int Seconds)> days, int goalDays, DateOnly today)
    {
        goalDays = Math.Clamp(goalDays, 1, 7);
        var activeDates = days.Where(d => d.Seconds >= ActiveDayThresholdSeconds).Select(d => d.Date).ToHashSet();

        var thisWeek = WeekStart(today);
        var daysThisWeek = activeDates.Count(d => d >= thisWeek && d <= today);

        var streak = daysThisWeek >= goalDays ? 1 : 0;
        for (var week = thisWeek.AddDays(-7); ; week = week.AddDays(-7))
        {
            var count = activeDates.Count(d => d >= week && d < week.AddDays(7));
            if (count < goalDays) break;
            streak++;
        }

        return new StreakInfo(streak, daysThisWeek, goalDays);
    }

    public static DateOnly WeekStart(DateOnly date)
    {
        var offset = ((int)date.DayOfWeek + 6) % 7; // Monday = 0
        return date.AddDays(-offset);
    }
}
