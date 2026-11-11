using System.Text.Json;
using Codegrove.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Codegrove.Api.Features.Catalog;

public sealed record TrackSummaryDto(
    string Slug, string Title, string Description, AgeBand AgeBand, int CourseCount, IReadOnlyList<string> Prerequisites);

public sealed record CourseSummaryDto(
    string Slug, string Title, string Summary, int EstimatedMinutes, bool IsPublished, int LessonCount, int LessonsCompleted);

public sealed record TrackDetailDto(
    string Slug, string Title, string Description, AgeBand AgeBand,
    IReadOnlyList<TrackSummaryDto> Prerequisites, IReadOnlyList<CourseSummaryDto> Courses, string? CertificateCode);

public sealed record LessonSummaryDto(
    int Id, string Slug, string Title, int EstimatedMinutes, bool IsPublished, bool Completed);

public sealed record CourseDetailDto(
    string Slug, string Title, string Summary, int EstimatedMinutes, bool IsPublished,
    IReadOnlyList<string> Objectives, IReadOnlyList<LessonSummaryDto> Lessons);

/// <summary>
/// Public catalog. Anonymous visitors can browse (parents want to see what kids will learn);
/// signed-in learners get their own band and progress.
/// </summary>
[ApiController]
[Route("api")]
public sealed class CatalogController(AppDbContext db) : ControllerBase
{
    [HttpGet("tracks")]
    public async Task<IReadOnlyList<TrackSummaryDto>> Tracks([FromQuery] AgeBand? band, CancellationToken ct)
    {
        var effective = EffectiveBand(band);
        return await db.Tracks.AsNoTracking()
            .Where(t => t.IsPublished && (effective == null || t.AgeBand == AgeBand.Both || t.AgeBand == effective))
            .OrderBy(t => t.SortOrder)
            .Select(t => new TrackSummaryDto(t.Slug, t.Title, t.Description, t.AgeBand, t.Courses.Count,
                t.Prerequisites.Select(p => p.RequiresTrack.Slug).ToList()))
            .ToListAsync(ct);
    }

    [HttpGet("tracks/{slug}")]
    public async Task<ActionResult<TrackDetailDto>> Track(string slug, CancellationToken ct)
    {
        var track = await db.Tracks.AsNoTracking()
            .Where(t => t.Slug == slug && t.IsPublished)
            .Select(t => new
            {
                t.Id, t.Slug, t.Title, t.Description, t.AgeBand,
                Prereqs = t.Prerequisites.Select(p => new TrackSummaryDto(p.RequiresTrack.Slug, p.RequiresTrack.Title,
                    p.RequiresTrack.Description, p.RequiresTrack.AgeBand, p.RequiresTrack.Courses.Count, new List<string>())).ToList(),
                Courses = t.Courses.OrderBy(c => c.SortOrder).Select(c => c.Course).ToList(),
            })
            .SingleOrDefaultAsync(ct);
        if (track is null) return NotFound();

        var band = EffectiveBand(null);
        var userId = LearnerId();
        var courses = new List<CourseSummaryDto>();
        foreach (var c in track.Courses)
        {
            var lessonIds = await LessonIdsFor(c.Id, band, ct);
            courses.Add(new CourseSummaryDto(c.Slug, c.Title, c.Summary, c.EstimatedMinutes, c.IsPublished,
                lessonIds.Count, await CompletedCount(userId, lessonIds, ct)));
        }

        string? cert = userId is null ? null : await db.Certificates
            .Where(x => x.UserId == userId && x.TrackId == track.Id)
            .Select(x => x.VerificationCode).SingleOrDefaultAsync(ct);

        return new TrackDetailDto(track.Slug, track.Title, track.Description, track.AgeBand, track.Prereqs, courses, cert);
    }

    [HttpGet("courses/{slug}")]
    public async Task<ActionResult<CourseDetailDto>> Course(string slug, CancellationToken ct)
    {
        var course = await db.Courses.AsNoTracking().SingleOrDefaultAsync(c => c.Slug == slug, ct);
        if (course is null) return NotFound();

        var band = EffectiveBand(null);
        var userId = LearnerId();
        var completed = userId is null
            ? []
            : await db.LessonProgress
                .Where(p => p.UserId == userId && p.CompletedAt != null && p.Lesson.CourseId == course.Id)
                .Select(p => p.LessonId).ToListAsync(ct);

        // Unpublished lessons are listed (as "coming soon") so learners see the whole course outline.
        var lessons = await db.Lessons.AsNoTracking()
            .Where(l => l.CourseId == course.Id && (band == null || l.AgeBand == AgeBand.Both || l.AgeBand == band))
            .OrderBy(l => l.SortOrder).ThenBy(l => l.AgeBand)
            .Select(l => new LessonSummaryDto(l.Id, l.Slug, l.Title, l.EstimatedMinutes, l.IsPublished && course.IsPublished, false))
            .ToListAsync(ct);

        return new CourseDetailDto(course.Slug, course.Title, course.Summary, course.EstimatedMinutes, course.IsPublished,
            JsonSerializer.Deserialize<List<string>>(course.ObjectivesJson) ?? [],
            lessons.Select(l => l with { Completed = completed.Contains(l.Id) }).ToList());
    }

    /// <summary>Signed-in learners always see their own band; others can pick one with ?band=.</summary>
    private AgeBand? EffectiveBand(AgeBand? requested) =>
        User.IsInRole(Roles.Learner) ? User.Band() : requested is AgeBand.Explorer or AgeBand.Builder ? requested : null;

    private Guid? LearnerId() => User.IsInRole(Roles.Learner) ? User.UserId() : null;

    private Task<List<int>> LessonIdsFor(int courseId, AgeBand? band, CancellationToken ct) =>
        db.Lessons.Where(l => l.CourseId == courseId && l.IsPublished
                              && (band == null || l.AgeBand == AgeBand.Both || l.AgeBand == band))
            .Select(l => l.Id).ToListAsync(ct);

    private async Task<int> CompletedCount(Guid? userId, List<int> lessonIds, CancellationToken ct) =>
        userId is null ? 0 : await db.LessonProgress.CountAsync(
            p => p.UserId == userId && lessonIds.Contains(p.LessonId) && p.CompletedAt != null, ct);
}
