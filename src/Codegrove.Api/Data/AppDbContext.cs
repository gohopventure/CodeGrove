using Codegrove.Api.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Codegrove.Api.Data;

/// <summary>
/// Single EF Core context for the app. Identity tables come from the base class.
/// Production runs on SQL Server (Azure SQL); integration tests run on SQLite in memory,
/// so configuration here avoids provider-specific SQL.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    // Identity and family
    public DbSet<ParentChildLink> ParentChildLinks => Set<ParentChildLink>();
    public DbSet<ParentalConsent> ParentalConsents => Set<ParentalConsent>();
    public DbSet<LearnerSettings> LearnerSettings => Set<LearnerSettings>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();

    // Catalog
    public DbSet<Track> Tracks => Set<Track>();
    public DbSet<TrackPrerequisite> TrackPrerequisites => Set<TrackPrerequisite>();
    public DbSet<Course> Courses => Set<Course>();
    public DbSet<TrackCourse> TrackCourses => Set<TrackCourse>();
    public DbSet<Lesson> Lessons => Set<Lesson>();
    public DbSet<Challenge> Challenges => Set<Challenge>();
    public DbSet<TestCase> TestCases => Set<TestCase>();
    public DbSet<Hint> Hints => Set<Hint>();
    public DbSet<LessonScript> LessonScripts => Set<LessonScript>();
    public DbSet<TutorCharacter> TutorCharacters => Set<TutorCharacter>();
    public DbSet<TutorLine> TutorLines => Set<TutorLine>();
    public DbSet<Quiz> Quizzes => Set<Quiz>();
    public DbSet<QuizQuestion> QuizQuestions => Set<QuizQuestion>();
    public DbSet<QuizOption> QuizOptions => Set<QuizOption>();

    // Learner activity
    public DbSet<Submission> Submissions => Set<Submission>();
    public DbSet<HintReveal> HintReveals => Set<HintReveal>();
    public DbSet<SolutionReveal> SolutionReveals => Set<SolutionReveal>();
    public DbSet<QuizAttempt> QuizAttempts => Set<QuizAttempt>();
    public DbSet<LessonProgress> LessonProgress => Set<LessonProgress>();
    public DbSet<DailyActivity> DailyActivity => Set<DailyActivity>();

    // Motivation
    public DbSet<XpEvent> XpEvents => Set<XpEvent>();
    public DbSet<Badge> Badges => Set<Badge>();
    public DbSet<UserBadge> UserBadges => Set<UserBadge>();
    public DbSet<Certificate> Certificates => Set<Certificate>();
    public DbSet<PortfolioEntry> PortfolioEntries => Set<PortfolioEntry>();

    /// <summary>
    /// Every timestamp is stored in UTC. SQL Server and SQLite don't keep DateTime.Kind, so values read
    /// back are marked UTC here; that makes the API serialize them with a trailing "Z" and browsers
    /// convert them to the learner's local time (otherwise a 5:35 AM UTC award shows as 5:35 AM local).
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configuration)
    {
        configuration.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configuration.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime()) : v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        ConfigureIdentityAndFamily(b);
        ConfigureCatalog(b);
        ConfigureActivity(b);
        ConfigureMotivation(b);
    }

    private static void ConfigureIdentityAndFamily(ModelBuilder b)
    {
        b.Entity<AppUser>(e =>
        {
            e.Property(u => u.AgeBand).HasConversion<string>().HasMaxLength(16);
            e.Property(u => u.Status).HasConversion<string>().HasMaxLength(16);
        });

        // Two FKs to AspNetUsers: SQL Server rejects two cascade paths, so both are Restrict
        // and the parent-delete flow removes links explicitly.
        b.Entity<ParentChildLink>(e =>
        {
            e.HasKey(x => new { x.ParentUserId, x.ChildUserId });
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Child).WithMany().HasForeignKey(x => x.ChildUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.ChildUserId);
        });

        b.Entity<ParentalConsent>(e =>
        {
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Child).WithMany().HasForeignKey(x => x.ChildUserId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Method).HasConversion<string>().HasMaxLength(24);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.NoticeVersion).HasMaxLength(32);
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.HasIndex(x => x.TokenHash);
            e.HasIndex(x => new { x.ChildUserId, x.Status });
        });

        b.Entity<LearnerSettings>(e =>
        {
            e.HasKey(x => x.UserId);
            e.HasOne(x => x.User).WithOne(u => u.Settings)
                .HasForeignKey<LearnerSettings>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AuditLogEntry>(e =>
        {
            e.ToTable("AuditLog");
            e.Property(x => x.Action).HasMaxLength(64);
            e.Property(x => x.TargetType).HasMaxLength(64);
            e.Property(x => x.TargetId).HasMaxLength(64);
            e.HasIndex(x => x.CreatedAt);
        });
    }

    private static void ConfigureCatalog(ModelBuilder b)
    {
        b.Entity<Track>(e =>
        {
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Slug).HasMaxLength(80);
            e.Property(x => x.Title).HasMaxLength(120);
            e.Property(x => x.AgeBand).HasConversion<string>().HasMaxLength(16);
        });

        b.Entity<TrackPrerequisite>(e =>
        {
            e.HasKey(x => new { x.TrackId, x.RequiresTrackId });
            e.HasOne(x => x.Track).WithMany(t => t.Prerequisites).HasForeignKey(x => x.TrackId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.RequiresTrack).WithMany().HasForeignKey(x => x.RequiresTrackId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Course>(e =>
        {
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Slug).HasMaxLength(80);
            e.Property(x => x.Title).HasMaxLength(120);
            e.Property(x => x.AgeBand).HasConversion<string>().HasMaxLength(16);
        });

        b.Entity<TrackCourse>(e =>
        {
            e.HasKey(x => new { x.TrackId, x.CourseId });
            e.HasOne(x => x.Track).WithMany(t => t.Courses).HasForeignKey(x => x.TrackId);
            e.HasOne(x => x.Course).WithMany(c => c.Tracks).HasForeignKey(x => x.CourseId);
        });

        b.Entity<Lesson>(e =>
        {
            e.HasIndex(x => new { x.CourseId, x.Slug }).IsUnique();
            e.Property(x => x.Slug).HasMaxLength(80);
            e.Property(x => x.Title).HasMaxLength(160);
            e.Property(x => x.AgeBand).HasConversion<string>().HasMaxLength(16);
            e.HasOne(x => x.Course).WithMany(c => c.Lessons).HasForeignKey(x => x.CourseId);
        });

        b.Entity<Challenge>(e =>
        {
            e.HasIndex(x => x.LessonId).IsUnique();
            e.HasOne(x => x.Lesson).WithOne(l => l.Challenge).HasForeignKey<Challenge>(x => x.LessonId);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.PortfolioTheme).HasMaxLength(60);
        });

        b.Entity<TestCase>(e =>
        {
            e.Property(x => x.Kind).HasMaxLength(40);
            e.Property(x => x.Label).HasMaxLength(200);
            e.HasOne(x => x.Challenge).WithMany(c => c.TestCases).HasForeignKey(x => x.ChallengeId);
        });

        b.Entity<Hint>(e =>
        {
            e.HasIndex(x => new { x.ChallengeId, x.Level }).IsUnique();
            e.Property(x => x.SpeechText).HasMaxLength(1000);
            e.Property(x => x.Mood).HasConversion<string>().HasMaxLength(16);
            e.HasOne(x => x.Challenge).WithMany(c => c.Hints).HasForeignKey(x => x.ChallengeId);
        });

        b.Entity<LessonScript>(e =>
        {
            e.HasIndex(x => new { x.LessonId, x.Kind, x.SortOrder }).IsUnique();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.DisplayText).HasMaxLength(1000);
            e.Property(x => x.SpeechText).HasMaxLength(1000);
            e.Property(x => x.Mood).HasConversion<string>().HasMaxLength(16);
            e.HasOne(x => x.Lesson).WithMany(l => l.Scripts).HasForeignKey(x => x.LessonId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TutorCharacter>(e =>
        {
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasIndex(x => x.Band).IsUnique();
            e.Property(x => x.Slug).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(64);
            e.Property(x => x.Band).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.VoiceId).HasMaxLength(128);
            e.Property(x => x.PortraitUrl).HasMaxLength(256);
            e.Property(x => x.PersonaPrompt).HasMaxLength(4000);
        });

        b.Entity<TutorLine>(e =>
        {
            e.HasIndex(x => new { x.CharacterId, x.Event, x.SortOrder }).IsUnique();
            e.Property(x => x.Event).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Mood).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.DisplayText).HasMaxLength(500);
            e.Property(x => x.SpeechText).HasMaxLength(500);
            e.HasOne(x => x.Character).WithMany(c => c.Lines).HasForeignKey(x => x.CharacterId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Quiz>(e =>
        {
            e.HasIndex(x => x.LessonId).IsUnique();
            e.HasOne(x => x.Lesson).WithOne(l => l.Quiz).HasForeignKey<Quiz>(x => x.LessonId);
        });

        b.Entity<QuizQuestion>(e => e.HasOne(x => x.Quiz).WithMany(q => q.Questions).HasForeignKey(x => x.QuizId));
        b.Entity<QuizOption>(e => e.HasOne(x => x.Question).WithMany(q => q.Options).HasForeignKey(x => x.QuestionId));
    }

    private static void ConfigureActivity(ModelBuilder b)
    {
        b.Entity<Submission>(e =>
        {
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Challenge).WithMany().HasForeignKey(x => x.ChallengeId);
            e.Property(x => x.CheckedBy).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.UserId, x.ChallengeId, x.CreatedAt });
        });

        b.Entity<HintReveal>(e =>
        {
            e.HasKey(x => new { x.UserId, x.HintId });
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Hint).WithMany().HasForeignKey(x => x.HintId);
        });

        b.Entity<SolutionReveal>(e =>
        {
            e.HasKey(x => new { x.UserId, x.ChallengeId });
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Challenge).WithMany().HasForeignKey(x => x.ChallengeId);
        });

        b.Entity<QuizAttempt>(e =>
        {
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Quiz).WithMany().HasForeignKey(x => x.QuizId);
            e.HasIndex(x => new { x.UserId, x.QuizId });
        });

        b.Entity<LessonProgress>(e =>
        {
            e.HasKey(x => new { x.UserId, x.LessonId });
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Lesson).WithMany().HasForeignKey(x => x.LessonId);
        });

        b.Entity<DailyActivity>(e =>
        {
            e.HasKey(x => new { x.UserId, x.Date });
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureMotivation(ModelBuilder b)
    {
        b.Entity<XpEvent>(e =>
        {
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.SourceType).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.UserId, x.SourceType, x.SourceId }).IsUnique();
        });

        b.Entity<Badge>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(40);
            e.Property(x => x.RuleType).HasConversion<string>().HasMaxLength(24);
        });

        b.Entity<UserBadge>(e =>
        {
            e.HasKey(x => new { x.UserId, x.BadgeId });
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Badge).WithMany().HasForeignKey(x => x.BadgeId);
        });

        b.Entity<Certificate>(e =>
        {
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Track).WithMany().HasForeignKey(x => x.TrackId);
            e.HasIndex(x => new { x.UserId, x.TrackId }).IsUnique();
            e.HasIndex(x => x.VerificationCode).IsUnique();
            e.Property(x => x.VerificationCode).HasMaxLength(10);
            e.Property(x => x.UserNameAtIssue).HasMaxLength(64);
        });

        b.Entity<PortfolioEntry>(e =>
        {
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Challenge).WithMany().HasForeignKey(x => x.ChallengeId);
            // Submissions are deleted with the user, like the entry: NoAction avoids a second cascade path in SQL Server.
            e.HasOne(x => x.Submission).WithMany().HasForeignKey(x => x.SubmissionId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => new { x.UserId, x.ChallengeId }).IsUnique();
            e.Property(x => x.Title).HasMaxLength(PortfolioEntry.MaxTitleLength);
            e.Property(x => x.Theme).HasMaxLength(60);
        });
    }
}
