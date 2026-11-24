namespace Codegrove.Api.Data;

/// <summary>Which audience a user, track, course or lesson is aimed at.</summary>
public enum AgeBand
{
    /// <summary>About 10–13: very short lessons, pictures, frequent wins.</summary>
    Explorer = 0,
    /// <summary>About 14–17: longer lessons, real tools and terminology.</summary>
    Builder = 1,
    /// <summary>Parents, teachers and admins. Never assigned to content.</summary>
    Adult = 2,
    /// <summary>Content only: suitable for both learner bands.</summary>
    Both = 3,
}

public enum AccountStatus
{
    /// <summary>Under-13 account created by a parent, waiting for consent. Cannot sign in.</summary>
    PendingConsent = 0,
    Active = 1,
    /// <summary>Consent revoked or an admin action. Cannot sign in; data is scheduled for deletion.</summary>
    Suspended = 2,
}

public enum ConsentMethod
{
    /// <summary>
    /// "Email plus": email the parent, then require a confirming action. COPPA allows this only
    /// when the child's data is used internally and not disclosed to third parties.
    /// </summary>
    EmailPlus = 0,
    Card = 1,
    GovernmentId = 2,
    VideoCall = 3,
    KnowledgeBased = 4,
}

public enum ConsentStatus { Pending = 0, Granted = 1, Revoked = 2, Expired = 3 }

/// <summary>
/// What a challenge's files are, so the app knows how to run them. Stored as a string, so adding a
/// kind needs no migration. TypeScript: .ts files run with their types stripped and are type-checked.
/// </summary>
public enum ChallengeKind { Html = 0, Css = 1, Js = 2, Sql = 3, CSharp = 4, React = 5, TypeScript = 6, Angular = 7, FullStack = 8 }

/// <summary>
/// Which part of a lesson the tutor says a line in. Intro plays before the lesson, Recap after it.
/// Stored as a string, so adding a kind later (e.g. a "stuck" pep talk) needs no migration.
/// </summary>
public enum LessonScriptKind { Intro = 0, Recap = 1 }

/// <summary>
/// The tutor's expression for a line. In the MVP it picks the portrait shown next to the text; later it drives the
/// avatar's face. Stored as a string.
/// </summary>
public enum TutorMood { Neutral = 0, Encouraging = 1, Thinking = 2, Celebrating = 3 }

/// <summary>
/// Moments in a lesson when the tutor says one of its reusable lines (<see cref="Entities.TutorLine"/>). The lesson's
/// own intro and recap (<see cref="Entities.LessonScript"/>) cover the start and end of a lesson. Stored as a string.
/// </summary>
public enum TutorEvent
{
    /// <summary>The first failed check in a challenge: a short reassurance, never a hint.</summary>
    FirstFail = 0,
    /// <summary>After 3 failed checks or about 4 minutes without progress: offers a hint, never gives one.</summary>
    StuckOffer = 1,
    /// <summary>The challenge passes: a short celebration of the work, not the kid.</summary>
    Pass = 2,
}

public enum SubmissionCheckedBy { Client = 0, Server = 1 }

public enum XpSourceType { Challenge = 0, Quiz = 1, Lesson = 2, Course = 3 }

public enum BadgeRuleType
{
    LessonsCompleted = 0,
    CourseCompleted = 1,
    FirstTryPass = 2,
    WeeklyGoalStreak = 3,
}
