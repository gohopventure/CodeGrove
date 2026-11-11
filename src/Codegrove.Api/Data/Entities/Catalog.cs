namespace Codegrove.Api.Data.Entities;

// Authored content. Integer keys keep seed files and authoring URLs readable.
// Slugs are the stable identity used by the content seeder to upsert.

public class Track
{
    public int Id { get; set; }
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public AgeBand AgeBand { get; set; } = AgeBand.Both;
    public int SortOrder { get; set; }
    public bool IsPublished { get; set; }

    public List<TrackCourse> Courses { get; set; } = [];
    public List<TrackPrerequisite> Prerequisites { get; set; } = [];
}

public class TrackPrerequisite
{
    public int TrackId { get; set; }
    public Track Track { get; set; } = null!;
    public int RequiresTrackId { get; set; }
    public Track RequiresTrack { get; set; } = null!;
}

public class Course
{
    public int Id { get; set; }
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public AgeBand AgeBand { get; set; } = AgeBand.Both;
    public int EstimatedMinutes { get; set; }

    /// <summary>JSON array of strings. Read-only display data, so a child table isn't worth it.</summary>
    public string ObjectivesJson { get; set; } = "[]";

    public bool IsPublished { get; set; }
    public int Version { get; set; } = 1;

    public List<Lesson> Lessons { get; set; } = [];
    public List<TrackCourse> Tracks { get; set; } = [];
}

public class TrackCourse
{
    public int TrackId { get; set; }
    public Track Track { get; set; } = null!;
    public int CourseId { get; set; }
    public Course Course { get; set; } = null!;
    public int SortOrder { get; set; }
}

public class Lesson
{
    public int Id { get; set; }
    public int CourseId { get; set; }
    public Course Course { get; set; } = null!;
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public int SortOrder { get; set; }

    /// <summary>Explorer, Builder or Both. A course shows learners only the lessons for their band.</summary>
    public AgeBand AgeBand { get; set; } = AgeBand.Both;
    public int EstimatedMinutes { get; set; }

    public string LearnMarkdown { get; set; } = "";
    public string? VideoUrl { get; set; }
    /// <summary>WebVTT captions. Required whenever <see cref="VideoUrl"/> is set.</summary>
    public string? CaptionsUrl { get; set; }

    public string ExampleMarkdown { get; set; } = "";
    public string? ExampleCode { get; set; }

    public bool IsPublished { get; set; }

    public Challenge? Challenge { get; set; }
    public Quiz? Quiz { get; set; }
}

public class Challenge
{
    public int Id { get; set; }
    public int LessonId { get; set; }
    public Lesson Lesson { get; set; } = null!;
    public ChallengeKind Kind { get; set; }
    public string InstructionsMarkdown { get; set; } = "";

    /// <summary>JSON object: file name → contents, e.g. {"index.html":"..."}.</summary>
    public string StarterFilesJson { get; set; } = "{}";

    /// <summary>Same shape as starter files. Only ever sent after hint level 3 is revealed.</summary>
    public string SolutionFilesJson { get; set; } = "{}";

    public int XpReward { get; set; } = 10;

    public List<TestCase> TestCases { get; set; } = [];
    public List<Hint> Hints { get; set; } = [];
}

public class TestCase
{
    public int Id { get; set; }
    public int ChallengeId { get; set; }
    public Challenge Challenge { get; set; } = null!;
    public int SortOrder { get; set; }

    /// <summary>Kid-facing statement shown when the test passes, e.g. "Your page has an &lt;h1&gt; heading".</summary>
    public string Label { get; set; } = "";

    /// <summary>Test kind understood by the preview harness, e.g. "elementExists".</summary>
    public string Kind { get; set; } = "";

    /// <summary>Kind-specific parameters as JSON, e.g. {"selector":"h1"}.</summary>
    public string SpecJson { get; set; } = "{}";

    /// <summary>Friendly message shown when the test fails.</summary>
    public string FailMessage { get; set; } = "";
}

public class Hint
{
    public int Id { get; set; }
    public int ChallengeId { get; set; }
    public Challenge Challenge { get; set; } = null!;

    /// <summary>1 = nudge, 2 = more help, 3 = the solution.</summary>
    public int Level { get; set; }
    public string Markdown { get; set; } = "";
}

public class Quiz
{
    public int Id { get; set; }
    public int LessonId { get; set; }
    public Lesson Lesson { get; set; } = null!;
    public int PassPercent { get; set; } = 60;
    public List<QuizQuestion> Questions { get; set; } = [];
}

public class QuizQuestion
{
    public int Id { get; set; }
    public int QuizId { get; set; }
    public Quiz Quiz { get; set; } = null!;
    public int SortOrder { get; set; }
    public string Prompt { get; set; } = "";
    /// <summary>Shown after the learner answers, right or wrong.</summary>
    public string Explanation { get; set; } = "";
    public List<QuizOption> Options { get; set; } = [];
}

public class QuizOption
{
    public int Id { get; set; }
    public int QuestionId { get; set; }
    public QuizQuestion Question { get; set; } = null!;
    public int SortOrder { get; set; }
    public string Text { get; set; } = "";
    /// <summary>Never serialized to learners; quizzes are graded on the server.</summary>
    public bool IsCorrect { get; set; }
}
