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
    public TrackCompletionRule CompletionRule { get; set; } = TrackCompletionRule.AllCourses;

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

    /// <summary>What the tutor says: a spoken-style intro and recap (see <see cref="LessonScript"/>).</summary>
    public List<LessonScript> Scripts { get; set; } = [];
}

/// <summary>
/// One segment of the tutor's script for a lesson. Today the lesson player shows <see cref="DisplayText"/> in the tutor
/// panel; later a Tutor service voices <see cref="SpeechText"/> (pre-rendered intro/recap clips first, live avatar later)
/// by reading this table. Exercise hints live in <see cref="Hint"/>, one row per level.
/// </summary>
public class LessonScript
{
    public int Id { get; set; }
    public int LessonId { get; set; }
    public Lesson Lesson { get; set; } = null!;
    public LessonScriptKind Kind { get; set; }

    /// <summary>Order within the kind, from 0. Lets a script be split into separately voiced segments.</summary>
    public int SortOrder { get; set; }

    /// <summary>The caption shown in the tutor panel. Light markdown is allowed, e.g. `&lt;ul&gt;` in backticks.</summary>
    public string DisplayText { get; set; } = "";

    /// <summary>What the voice says: plain words only, no code symbols, acronyms spelled for speech ("u-l", "C sharp").</summary>
    public string SpeechText { get; set; } = "";

    public TutorMood Mood { get; set; } = TutorMood.Neutral;
}

/// <summary>
/// A tutor character. One per learner band: Juni for Explorers, Rowan for Builders. Content never names the
/// tutor, so renaming a character only changes Content/tutors.json.
/// </summary>
public class TutorCharacter
{
    public int Id { get; set; }
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Explorer or Builder. Each band has exactly one character.</summary>
    public AgeBand Band { get; set; }

    /// <summary>Text-to-speech voice for later (an Azure Neural TTS voice name). Unused in the MVP.</summary>
    public string VoiceId { get; set; } = "";

    /// <summary>Portrait path with a <c>{mood}</c> placeholder, e.g. <c>/tutor/juni-{mood}.svg</c> (mood in lower case).</summary>
    public string PortraitUrl { get; set; } = "";

    /// <summary>Persona and rules for the Phase 2 live tutor's model prompt. Never sent to the browser.</summary>
    public string PersonaPrompt { get; set; } = "";

    public List<TutorLine> Lines { get; set; } = [];
}

/// <summary>A reusable line a character says at a <see cref="TutorEvent"/>; the player picks one from the pool at random.</summary>
public class TutorLine
{
    public int Id { get; set; }
    public int CharacterId { get; set; }
    public TutorCharacter Character { get; set; } = null!;
    public TutorEvent Event { get; set; }
    public int SortOrder { get; set; }
    public string DisplayText { get; set; } = "";
    public string SpeechText { get; set; } = "";
    public TutorMood Mood { get; set; } = TutorMood.Neutral;
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

    /// <summary>
    /// Same shape as starter files. Only ever sent once the learner asks for it after all three hints
    /// (a <see cref="SolutionReveal"/>), or after they pass.
    /// </summary>
    public string SolutionFilesJson { get; set; } = "{}";

    public int XpReward { get; set; } = 10;

    /// <summary>
    /// Optional blocks mode (Explorer JavaScript lessons): JSON {"toolbox": [category keys], "starter": Blockly
    /// workspace state}. Null for text-only challenges. Any solution workspace in the content file stays out of
    /// the database, so it can never be sent to a learner.
    /// </summary>
    public string? BlocksJson { get; set; }

    /// <summary>
    /// Capstone milestones: the slug of the lesson (same course, same band) this challenge continues from. The
    /// learner starts from their own passing code for that lesson's challenge, for the files in
    /// <see cref="CarryFilesJson"/>; everything else comes from this challenge's starter. Null: starts fresh.
    /// </summary>
    public string? ContinuesFromLessonSlug { get; set; }

    /// <summary>JSON array of the file names carried over from <see cref="ContinuesFromLessonSlug"/>.</summary>
    public string? CarryFilesJson { get; set; }

    /// <summary>
    /// Capstone: passing this challenge puts the learner's app in their portfolio, under this theme name
    /// ("Grove Club Hub"). Null for every other challenge. Only FullStack challenges can have one.
    /// </summary>
    public string? PortfolioTheme { get; set; }

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

    /// <summary>
    /// 1 = nudge (always a guiding question), 2 = pointer (names the concept and where to look),
    /// 3 = nearly there (a partial fill-in). The full solution is never a hint; it's a separate step after level 3.
    /// </summary>
    public int Level { get; set; }

    /// <summary>What the tutor panel shows. Markdown, may include code.</summary>
    public string Markdown { get; set; } = "";

    /// <summary>The same hint written to be read aloud: plain words, no code symbols.</summary>
    public string SpeechText { get; set; } = "";

    public TutorMood Mood { get; set; } = TutorMood.Thinking;
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
