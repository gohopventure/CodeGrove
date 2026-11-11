namespace Codegrove.Api.Data.Entities;

/// <summary>
/// XP ledger. A unique index on (UserId, SourceType, SourceId) means XP is awarded once per item,
/// so retrying a challenge can't farm points.
/// </summary>
public class XpEvent
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public int Amount { get; set; }
    public XpSourceType SourceType { get; set; }
    public int SourceId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class Badge
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string IconUrl { get; set; } = "";
    public BadgeRuleType RuleType { get; set; }

    /// <summary>Meaning depends on <see cref="RuleType"/>: a count, or a course id for CourseCompleted.</summary>
    public int RuleValue { get; set; }
}

public class UserBadge
{
    public Guid UserId { get; set; }
    public int BadgeId { get; set; }
    public Badge Badge { get; set; } = null!;
    public DateTime AwardedAt { get; set; }
}

public class Certificate
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public int TrackId { get; set; }
    public Track Track { get; set; } = null!;

    /// <summary>Copied at issue time so a later rename doesn't change a printed certificate.</summary>
    public string UserNameAtIssue { get; set; } = "";

    /// <summary>10-character public code for the verify page. Reveals username, track and date only.</summary>
    public string VerificationCode { get; set; } = "";
    public DateTime IssuedAt { get; set; }
    public string? PdfBlobPath { get; set; }
}
