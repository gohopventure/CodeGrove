namespace Codegrove.Api.Data.Entities;

/// <summary>
/// A finished project in a learner's portfolio. Made (or refreshed) when the learner passes a challenge that has a
/// <see cref="Challenge.PortfolioTheme"/>: the capstone's last milestone. One entry per learner per challenge.
///
/// Privacy: portfolios are never public. Only the learner and their linked parents can see an entry.
/// </summary>
public class PortfolioEntry
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public int ChallengeId { get; set; }
    public Challenge Challenge { get; set; } = null!;

    /// <summary>The passing submission the entry shows and downloads: the learner's latest passing code.</summary>
    public Guid SubmissionId { get; set; }
    public Submission Submission { get; set; } = null!;

    /// <summary>
    /// The project's name: the page's &lt;title&gt; when it passes the content filter, otherwise the lesson title.
    /// The learner can rename it (filtered too).
    /// </summary>
    public string Title { get; set; } = "";

    /// <summary>True once the learner renamed it: a later pass then keeps their name instead of the page's title.</summary>
    public bool TitleChosenByLearner { get; set; }

    /// <summary>Copied from the challenge when the entry is made, e.g. "Grove Club Hub" or "Junior League".</summary>
    public string Theme { get; set; } = "";

    /// <summary>
    /// A PNG of the app's page, taken in the learner's browser right after they pass (see web/src/runner/snapshot.ts).
    /// TRADEOFF: stored in the database while pictures are this small (capped at <see cref="MaxScreenshotBytes"/>).
    /// Move to Blob storage with <see cref="Certificate.PdfBlobPath"/>-style paths if portfolios grow.
    /// </summary>
    public byte[]? ScreenshotPng { get; set; }
    public DateTime? ScreenshotTakenAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public const int MaxTitleLength = 60;
    public const int MaxScreenshotBytes = 1_000_000;
}
