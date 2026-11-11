namespace Codegrove.Api.Data.Entities;

public class Submission
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public int ChallengeId { get; set; }
    public Challenge Challenge { get; set; } = null!;

    /// <summary>File name → contents.</summary>
    public string FilesJson { get; set; } = "{}";
    /// <summary>Array of {testId, passed, message} as reported by the checker.</summary>
    public string ResultsJson { get; set; } = "[]";

    /// <summary>Computed on the server from <see cref="ResultsJson"/>: every test of the challenge passed.</summary>
    public bool Passed { get; set; }
    public SubmissionCheckedBy CheckedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class HintReveal
{
    public Guid UserId { get; set; }
    public int HintId { get; set; }
    public Hint Hint { get; set; } = null!;
    public DateTime RevealedAt { get; set; }
}

public class QuizAttempt
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public int QuizId { get; set; }
    public Quiz Quiz { get; set; } = null!;
    public int ScorePercent { get; set; }
    public bool Passed { get; set; }
    /// <summary>Map of question id → chosen option id.</summary>
    public string AnswersJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
}

/// <summary>A lesson is complete when its challenge and quiz have both been passed.</summary>
public class LessonProgress
{
    public Guid UserId { get; set; }
    public int LessonId { get; set; }
    public Lesson Lesson { get; set; } = null!;
    public DateTime? LearnViewedAt { get; set; }
    public DateTime? ChallengePassedAt { get; set; }
    public DateTime? QuizPassedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

/// <summary>One row per learner per local calendar day. Feeds time-spent charts, limits and streaks.</summary>
public class DailyActivity
{
    public Guid UserId { get; set; }
    public DateOnly Date { get; set; }
    public int ActiveSeconds { get; set; }
    public int LessonsCompleted { get; set; }
}
