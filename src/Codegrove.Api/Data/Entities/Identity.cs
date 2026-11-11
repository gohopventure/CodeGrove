using Microsoft.AspNetCore.Identity;

namespace Codegrove.Api.Data.Entities;

/// <summary>
/// Every account: learners, parents, teachers and admins. Roles live in the standard Identity tables.
/// Deliberately has no real-name, photo, school or location columns (COPPA data minimization).
/// For learners under 13, <see cref="IdentityUser{TKey}.Email"/> is always null.
/// </summary>
public class AppUser : IdentityUser<Guid>
{
    public AgeBand AgeBand { get; set; }

    /// <summary>Learners only. Month + year is enough to know when a child turns 13.</summary>
    public short? BirthYear { get; set; }
    public byte? BirthMonth { get; set; }

    public AccountStatus Status { get; set; } = AccountStatus.Active;

    public bool UseDyslexiaFont { get; set; }
    public bool ReducedMotion { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? LastActiveAt { get; set; }

    public LearnerSettings? Settings { get; set; }
}

/// <summary>Links a parent/guardian to a child. A child can have up to two guardians.</summary>
public class ParentChildLink
{
    public Guid ParentUserId { get; set; }
    public AppUser Parent { get; set; } = null!;
    public Guid ChildUserId { get; set; }
    public AppUser Child { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// One verifiable-parental-consent record. The child's account becomes Active only when a row is Granted.
/// Disclosure to third parties (e.g. a future AI tutor) is a separate flag, as the amended COPPA Rule requires.
/// </summary>
public class ParentalConsent
{
    public Guid Id { get; set; }
    public Guid ParentUserId { get; set; }
    public AppUser Parent { get; set; } = null!;
    public Guid ChildUserId { get; set; }
    public AppUser Child { get; set; } = null!;

    public ConsentMethod Method { get; set; }
    public ConsentStatus Status { get; set; }
    public bool AllowThirdPartyDisclosure { get; set; }

    /// <summary>Version of the direct notice the parent saw, so we can prove what they agreed to.</summary>
    public string NoticeVersion { get; set; } = "";

    /// <summary>SHA-256 of the emailed token. The raw token is never stored.</summary>
    public string? TokenHash { get; set; }
    public DateTime? TokenExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? GrantedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

/// <summary>Per-learner settings, edited by a linked parent (or by a teen with no linked parent).</summary>
public class LearnerSettings
{
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;

    /// <summary>Null means no limit.</summary>
    public int? DailyMinutesLimit { get; set; }

    /// <summary>Streaks count weeks in which the learner was active on at least this many days.</summary>
    public int WeeklyGoalDays { get; set; } = 3;

    public bool WeeklyDigest { get; set; } = true;

    /// <summary>Stored now, enforced when the features ship. Both default to off.</summary>
    public bool AllowAiTutor { get; set; }
    public bool AllowCommunity { get; set; }
}

/// <summary>Append-only record of consent, role and deletion events.</summary>
public class AuditLogEntry
{
    public long Id { get; set; }
    public Guid? ActorUserId { get; set; }
    public string Action { get; set; } = "";
    public string TargetType { get; set; } = "";
    public string TargetId { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string? DetailsJson { get; set; }
}
