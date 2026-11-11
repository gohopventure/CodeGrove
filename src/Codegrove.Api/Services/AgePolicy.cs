using Codegrove.Api.Data;

namespace Codegrove.Api.Services;

/// <summary>
/// Age rules in one place. We store birth month and year only, so when today's month equals the
/// birth month we assume the birthday hasn't happened yet. That errs toward "younger", which is
/// the safe direction for COPPA.
/// </summary>
public static class AgePolicy
{
    public const int CoppaAge = 13;
    public const int MinimumLearnerAge = 7;
    public const int ExplorerMaxAge = 13;

    public static int AgeInYears(int birthYear, int birthMonth, DateOnly today)
    {
        var age = today.Year - birthYear;
        if (today.Month <= birthMonth) age--;
        return age;
    }

    public static bool IsUnder13(int birthYear, int birthMonth, DateOnly today) =>
        AgeInYears(birthYear, birthMonth, today) < CoppaAge;

    /// <summary>Default band for a learner; a parent can override it.</summary>
    public static AgeBand DefaultBand(int birthYear, int birthMonth, DateOnly today) =>
        AgeInYears(birthYear, birthMonth, today) <= ExplorerMaxAge ? AgeBand.Explorer : AgeBand.Builder;

    public static bool IsValidBirthDate(int birthYear, int birthMonth, DateOnly today) =>
        birthMonth is >= 1 and <= 12 && birthYear >= 1900 && birthYear <= today.Year;
}
