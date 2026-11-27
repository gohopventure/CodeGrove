using System.Security.Cryptography;
using System.Text.Json;
using Codegrove.Api.Data;
using Codegrove.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Codegrove.Api.Services;

public sealed record TestResultInput(int TestId, bool Passed, string? Message);

public sealed record AwardedBadge(string Code, string Name, string Description, string IconUrl);

public sealed record ProgressOutcome(
    int XpAwarded,
    int TotalXp,
    int Level,
    IReadOnlyList<AwardedBadge> NewBadges,
    bool LessonCompleted,
    bool CourseCompleted,
    string? CertificateCode,
    Guid? PortfolioEntryId = null);

/// <summary>
/// Owns every rule about progress: when a challenge counts as passed, when a lesson, course and
/// track are complete, how much XP is awarded, and which badges and certificates are earned.
/// Each public method runs in one transaction so a learner never sees half an award.
/// </summary>
public sealed class ProgressService(AppDbContext db, TimeProvider clock, IContentFilter filter)
{
    public const int QuizXp = 5;
    public const int LessonXp = 5;
    public const int CourseXp = 50;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Stores a challenge submission. "Passed" is computed here from the reported results:
    /// every test of the challenge must be present and passing.
    /// </summary>
    public Task<(Submission Submission, ProgressOutcome Outcome)> RecordSubmissionAsync(
        Guid userId, AgeBand band, Challenge challenge,
        IReadOnlyDictionary<string, string> files, IReadOnlyList<TestResultInput> results,
        CancellationToken ct = default) =>
        WithConflictRetryAsync(() => RecordSubmissionCoreAsync(userId, band, challenge, files, results, ct));

    private async Task<(Submission Submission, ProgressOutcome Outcome)> RecordSubmissionCoreAsync(
        Guid userId, AgeBand band, Challenge challenge,
        IReadOnlyDictionary<string, string> files, IReadOnlyList<TestResultInput> results,
        CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var acc = new Accumulator();

        var requiredIds = challenge.TestCases.Select(t => t.Id).ToHashSet();
        var passedIds = results.Where(r => r.Passed).Select(r => r.TestId).ToHashSet();
        var passed = requiredIds.Count > 0 && requiredIds.IsSubsetOf(passedIds);

        var submission = new Submission
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ChallengeId = challenge.Id,
            FilesJson = JsonSerializer.Serialize(files, Json),
            // Only keep results for this challenge's tests; ignore anything else the client sent.
            ResultsJson = JsonSerializer.Serialize(results.Where(r => requiredIds.Contains(r.TestId)), Json),
            Passed = passed,
            CheckedBy = SubmissionCheckedBy.Client,
            CreatedAt = now,
        };
        db.Submissions.Add(submission);

        if (passed)
        {
            var progress = await GetOrCreateProgressAsync(userId, challenge.LessonId, ct);
            progress.ChallengePassedAt ??= now;
            acc.Xp += await AwardXpAsync(userId, XpSourceType.Challenge, challenge.Id, challenge.XpReward, now, ct);
            await db.SaveChangesAsync(ct);
            await CompleteLessonIfDoneAsync(userId, band, challenge.LessonId, progress, acc, now, ct);
            if (challenge.PortfolioTheme is not null) acc.PortfolioEntryId = await UpsertPortfolioEntryAsync(userId, challenge, submission, files, now, ct);
        }

        await db.SaveChangesAsync(ct);
        var outcome = await FinishAsync(userId, acc, now, ct);
        await tx.CommitAsync(ct);
        return (submission, outcome);
    }

    public Task<(QuizGrade Grade, ProgressOutcome Outcome)> RecordQuizAttemptAsync(
        Guid userId, AgeBand band, Quiz quiz, IReadOnlyDictionary<int, int> answers, CancellationToken ct = default) =>
        WithConflictRetryAsync(() => RecordQuizAttemptCoreAsync(userId, band, quiz, answers, ct));

    private async Task<(QuizGrade Grade, ProgressOutcome Outcome)> RecordQuizAttemptCoreAsync(
        Guid userId, AgeBand band, Quiz quiz, IReadOnlyDictionary<int, int> answers, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var acc = new Accumulator();

        var grade = QuizGrader.Grade(quiz, answers);
        db.QuizAttempts.Add(new QuizAttempt
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            QuizId = quiz.Id,
            ScorePercent = grade.ScorePercent,
            Passed = grade.Passed,
            AnswersJson = JsonSerializer.Serialize(answers, Json),
            CreatedAt = now,
        });

        if (grade.Passed)
        {
            var progress = await GetOrCreateProgressAsync(userId, quiz.LessonId, ct);
            progress.QuizPassedAt ??= now;
            acc.Xp += await AwardXpAsync(userId, XpSourceType.Quiz, quiz.Id, QuizXp, now, ct);
            await db.SaveChangesAsync(ct);
            await CompleteLessonIfDoneAsync(userId, band, quiz.LessonId, progress, acc, now, ct);
        }

        await db.SaveChangesAsync(ct);
        var outcome = await FinishAsync(userId, acc, now, ct);
        await tx.CommitAsync(ct);
        return (grade, outcome);
    }

    public Task MarkLearnViewedAsync(Guid userId, int lessonId, CancellationToken ct = default) =>
        WithConflictRetryAsync(async () =>
        {
            var progress = await GetOrCreateProgressAsync(userId, lessonId, ct);
            progress.LearnViewedAt ??= clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
            return true;
        });

    /// <summary>
    /// Two requests for the same learner can race (a double-click, or React's dev-mode double effects),
    /// and both try to insert the same progress or XP row. The loser hits a unique-key violation;
    /// its transaction has rolled back, so we clear tracked state and run it once more, when the
    /// winner's rows are visible and the "already exists" checks make the retry a no-op.
    /// </summary>
    private async Task<T> WithConflictRetryAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return await action();
        }
    }

    public async Task<int> TotalXpAsync(Guid userId, CancellationToken ct = default) =>
        await db.XpEvents.Where(x => x.UserId == userId).SumAsync(x => (int?)x.Amount, ct) ?? 0;

    /// <summary>
    /// A course is complete when every lesson for the learner's band is published and completed.
    /// Unpublished lessons block completion on purpose: a half-written course can't award a certificate.
    /// </summary>
    public async Task<bool> IsCourseCompleteAsync(Guid userId, AgeBand band, int courseId, CancellationToken ct = default)
    {
        var lessons = await db.Lessons
            .Where(l => l.CourseId == courseId && (l.AgeBand == AgeBand.Both || l.AgeBand == band))
            .Select(l => new { l.Id, l.IsPublished })
            .ToListAsync(ct);

        if (lessons.Count == 0 || lessons.Any(l => !l.IsPublished)) return false;

        var ids = lessons.Select(l => l.Id).ToList();
        var completed = await db.LessonProgress
            .CountAsync(p => p.UserId == userId && ids.Contains(p.LessonId) && p.CompletedAt != null, ct);
        return completed == ids.Count;
    }

    // ----------------------------------------------------------------------------------------

    private sealed class Accumulator
    {
        public int Xp;
        public bool LessonCompleted;
        public bool CourseCompleted;
        public string? CertificateCode;
        public Guid? PortfolioEntryId;
    }

    /// <summary>
    /// Puts the learner's passing capstone app in their portfolio, or points the existing entry at this newer passing
    /// code. The old picture is dropped then (it shows the old page); the client takes a new one straight after.
    /// A name the learner chose is kept; otherwise the name follows the page's &lt;title&gt;.
    /// </summary>
    private async Task<Guid> UpsertPortfolioEntryAsync(
        Guid userId, Challenge challenge, Submission submission, IReadOnlyDictionary<string, string> files,
        DateTime now, CancellationToken ct)
    {
        var entry = await db.PortfolioEntries.SingleOrDefaultAsync(e => e.UserId == userId && e.ChallengeId == challenge.Id, ct);
        if (entry is null)
        {
            entry = new PortfolioEntry { Id = Guid.NewGuid(), UserId = userId, ChallengeId = challenge.Id, CreatedAt = now };
            db.PortfolioEntries.Add(entry);
        }

        entry.SubmissionId = submission.Id;
        entry.Theme = challenge.PortfolioTheme!;
        entry.UpdatedAt = now;
        entry.ScreenshotPng = null;
        entry.ScreenshotTakenAt = null;
        if (!entry.TitleChosenByLearner)
        {
            var lessonTitle = await db.Lessons.Where(l => l.Id == challenge.LessonId).Select(l => l.Title).SingleAsync(ct);
            entry.Title = PortfolioNames.FromPage(files, lessonTitle, filter);
        }
        await db.SaveChangesAsync(ct);
        return entry.Id;
    }

    private async Task CompleteLessonIfDoneAsync(
        Guid userId, AgeBand band, int lessonId, LessonProgress progress, Accumulator acc, DateTime now, CancellationToken ct)
    {
        if (progress.CompletedAt is not null) return;

        var lesson = await db.Lessons
            .Where(l => l.Id == lessonId)
            .Select(l => new { l.Id, l.CourseId, HasChallenge = l.Challenge != null, HasQuiz = l.Quiz != null })
            .SingleAsync(ct);

        var challengeDone = !lesson.HasChallenge || progress.ChallengePassedAt is not null;
        var quizDone = !lesson.HasQuiz || progress.QuizPassedAt is not null;
        if (!challengeDone || !quizDone) return;

        progress.CompletedAt = now;
        acc.LessonCompleted = true;
        acc.Xp += await AwardXpAsync(userId, XpSourceType.Lesson, lesson.Id, LessonXp, now, ct);
        await IncrementLessonsCompletedTodayAsync(userId, now, ct);
        await db.SaveChangesAsync(ct);

        if (!await IsCourseCompleteAsync(userId, band, lesson.CourseId, ct)) return;

        acc.CourseCompleted = true;
        acc.Xp += await AwardXpAsync(userId, XpSourceType.Course, lesson.CourseId, CourseXp, now, ct);
        await db.SaveChangesAsync(ct);
        acc.CertificateCode = await IssueTrackCertificatesAsync(userId, band, lesson.CourseId, now, ct);
    }

    /// <summary>Issues a certificate for every published track this course belongs to that is now complete.</summary>
    private async Task<string?> IssueTrackCertificatesAsync(Guid userId, AgeBand band, int courseId, DateTime now, CancellationToken ct)
    {
        var tracks = await db.Tracks
            .Where(t => t.IsPublished && t.Courses.Any(c => c.CourseId == courseId))
            .Select(t => new
            {
                t.Id,
                Courses = t.Courses.Select(c => new { c.CourseId, c.Course.IsPublished }).ToList(),
            })
            .ToListAsync(ct);

        var userName = await db.Users.Where(u => u.Id == userId).Select(u => u.UserName).SingleAsync(ct);
        string? lastCode = null;

        foreach (var track in tracks)
        {
            if (track.Courses.Any(c => !c.IsPublished)) continue;
            if (await db.Certificates.AnyAsync(c => c.UserId == userId && c.TrackId == track.Id, ct)) continue;

            var allDone = true;
            foreach (var c in track.Courses)
            {
                if (!await IsCourseCompleteAsync(userId, band, c.CourseId, ct)) { allDone = false; break; }
            }
            if (!allDone) continue;

            lastCode = NewVerificationCode();
            db.Certificates.Add(new Certificate
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TrackId = track.Id,
                UserNameAtIssue = userName ?? "",
                VerificationCode = lastCode,
                IssuedAt = now,
            });
        }

        await db.SaveChangesAsync(ct);
        return lastCode;
    }

    private async Task<ProgressOutcome> FinishAsync(Guid userId, Accumulator acc, DateTime now, CancellationToken ct)
    {
        var newBadges = await EvaluateBadgesAsync(userId, now, ct);
        var totalXp = await TotalXpAsync(userId, ct);
        return new ProgressOutcome(
            acc.Xp, totalXp, Levels.LevelFor(totalXp), newBadges,
            acc.LessonCompleted, acc.CourseCompleted, acc.CertificateCode, acc.PortfolioEntryId);
    }

    /// <summary>Checks every badge the learner doesn't have yet and awards the ones now earned.</summary>
    private async Task<IReadOnlyList<AwardedBadge>> EvaluateBadgesAsync(Guid userId, DateTime now, CancellationToken ct)
    {
        var earnedIds = await db.UserBadges.Where(b => b.UserId == userId).Select(b => b.BadgeId).ToListAsync(ct);
        var candidates = await db.Badges.Where(b => !earnedIds.Contains(b.Id)).ToListAsync(ct);
        if (candidates.Count == 0) return [];

        var lessonsCompleted = await db.LessonProgress.CountAsync(p => p.UserId == userId && p.CompletedAt != null, ct);

        // Course XP is awarded exactly once, at completion, so it doubles as the completion record.
        var completedCourseIds = (await db.XpEvents
            .Where(x => x.UserId == userId && x.SourceType == XpSourceType.Course)
            .Select(x => x.SourceId)
            .ToListAsync(ct)).ToHashSet();

        // Per-learner submission history is small, so this is computed in memory for provider portability.
        var history = await db.Submissions
            .Where(s => s.UserId == userId)
            .Select(s => new { s.ChallengeId, s.CreatedAt, s.Passed })
            .ToListAsync(ct);
        var firstTryPasses = history
            .GroupBy(s => s.ChallengeId)
            .Count(g => g.OrderBy(s => s.CreatedAt).First().Passed);

        var streakWeeks = 0;
        if (candidates.Any(b => b.RuleType == BadgeRuleType.WeeklyGoalStreak))
        {
            var today = DateOnly.FromDateTime(now);
            var since = today.AddDays(-7 * 26);
            var goal = await db.LearnerSettings.Where(s => s.UserId == userId).Select(s => (int?)s.WeeklyGoalDays).SingleOrDefaultAsync(ct) ?? 3;
            var days = await db.DailyActivity
                .Where(d => d.UserId == userId && d.Date >= since)
                .Select(d => new { d.Date, d.ActiveSeconds })
                .ToListAsync(ct);
            streakWeeks = Streaks.Calculate(days.Select(d => (d.Date, d.ActiveSeconds)), goal, today).StreakWeeks;
        }

        var awarded = new List<AwardedBadge>();
        foreach (var badge in candidates)
        {
            var earned = badge.RuleType switch
            {
                BadgeRuleType.LessonsCompleted => lessonsCompleted >= badge.RuleValue,
                BadgeRuleType.CourseCompleted => completedCourseIds.Contains(badge.RuleValue),
                BadgeRuleType.FirstTryPass => firstTryPasses >= badge.RuleValue,
                BadgeRuleType.WeeklyGoalStreak => streakWeeks >= badge.RuleValue,
                _ => false,
            };
            if (!earned) continue;

            db.UserBadges.Add(new UserBadge { UserId = userId, BadgeId = badge.Id, AwardedAt = now });
            awarded.Add(new AwardedBadge(badge.Code, badge.Name, badge.Description, badge.IconUrl));
        }

        await db.SaveChangesAsync(ct);
        return awarded;
    }

    private async Task<int> AwardXpAsync(Guid userId, XpSourceType type, int sourceId, int amount, DateTime now, CancellationToken ct)
    {
        var already = db.XpEvents.Local.Any(x => x.UserId == userId && x.SourceType == type && x.SourceId == sourceId)
            || await db.XpEvents.AnyAsync(x => x.UserId == userId && x.SourceType == type && x.SourceId == sourceId, ct);
        if (already) return 0;

        db.XpEvents.Add(new XpEvent
        {
            Id = Guid.NewGuid(), UserId = userId, Amount = amount,
            SourceType = type, SourceId = sourceId, CreatedAt = now,
        });
        return amount;
    }

    private async Task<LessonProgress> GetOrCreateProgressAsync(Guid userId, int lessonId, CancellationToken ct)
    {
        var progress = await db.LessonProgress.FindAsync([userId, lessonId], ct);
        if (progress is not null) return progress;

        progress = new LessonProgress { UserId = userId, LessonId = lessonId };
        db.LessonProgress.Add(progress);
        return progress;
    }

    // TRADEOFF: lesson-completion counts use the UTC date. Time-spent uses the learner's local date
    // (sent with each heartbeat); this counter only feeds the parent's "lessons this week" total.
    private async Task IncrementLessonsCompletedTodayAsync(Guid userId, DateTime now, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(now);
        var row = await db.DailyActivity.FindAsync([userId, today], ct);
        if (row is null)
        {
            row = new DailyActivity { UserId = userId, Date = today };
            db.DailyActivity.Add(row);
        }
        row.LessonsCompleted++;
    }

    private static string NewVerificationCode()
    {
        // No 0/O/1/I so codes can be read off a printed certificate.
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return string.Create(10, alphabet, (span, a) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = a[RandomNumberGenerator.GetInt32(a.Length)];
        });
    }
}
