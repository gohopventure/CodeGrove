using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Codegrove.Api.Data;
using Codegrove.Api.Data.Entities;
using Codegrove.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Codegrove.Api.Features.Portfolio;

/// <summary>
/// One project in a portfolio. <paramref name="ScreenshotUrl"/> is null until the learner's browser has sent a picture;
/// it changes whenever the picture does, so browsers never show an old one.
/// </summary>
public sealed record PortfolioEntryDto(
    Guid Id, string Title, string Theme, string CourseTitle, int LessonId, string LessonTitle,
    DateTime CreatedAt, DateTime UpdatedAt, string? ScreenshotUrl);

public sealed record RenameEntryRequest([Required] string Title);

public sealed record ScreenshotRequest([Required] string DataUrl);

public sealed record PortfolioFilesResponse(string Title, IReadOnlyDictionary<string, string> Files);

/// <summary>
/// The portfolio: finished capstone apps, each with its name, theme, a picture of its page and its code for
/// Download project. Never public (no public profiles for minors): the learner sees their own entries, and a linked
/// parent sees their child's. Anyone else gets 404, so entry ids reveal nothing.
/// </summary>
[ApiController]
[Route("api")]
[Authorize]
public sealed class PortfolioController(AppDbContext db, ConsentService consent, IContentFilter filter, TimeProvider clock) : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [HttpGet("me/portfolio")]
    [Authorize(Policy = Policies.ActiveLearner)]
    public Task<List<PortfolioEntryDto>> Mine(CancellationToken ct) => EntriesFor(User.UserId(), ct);

    [HttpGet("parent/children/{childId:guid}/portfolio")]
    [Authorize(Policy = Policies.Parent)]
    public async Task<ActionResult<List<PortfolioEntryDto>>> Child(Guid childId, CancellationToken ct)
    {
        if (!await consent.IsParentOfAsync(User.UserId(), childId, ct)) return NotFound();
        return await EntriesFor(childId, ct);
    }

    /// <summary>Renames a project. The name goes through the same filter as everything else learners type.</summary>
    [HttpPut("me/portfolio/{id:guid}/title")]
    [Authorize(Policy = Policies.ActiveLearner)]
    public async Task<ActionResult<PortfolioEntryDto>> Rename(Guid id, RenameEntryRequest req, CancellationToken ct)
    {
        var userId = User.UserId();
        var entry = await db.PortfolioEntries.SingleOrDefaultAsync(e => e.Id == id && e.UserId == userId, ct);
        if (entry is null) return NotFound();

        var (name, problem) = PortfolioNames.Validate(req.Title, filter);
        if (name is null) return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]> { ["title"] = [problem!] }));

        entry.Title = name;
        entry.TitleChosenByLearner = true;
        entry.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return (await EntriesFor(userId, ct)).Single(e => e.Id == id);
    }

    /// <summary>Stores the picture of the app's page that the learner's browser took (a PNG data URL).</summary>
    [HttpPut("me/portfolio/{id:guid}/screenshot")]
    [Authorize(Policy = Policies.ActiveLearner)]
    [RequestSizeLimit(2_000_000)]
    public async Task<ActionResult<PortfolioEntryDto>> Screenshot(Guid id, ScreenshotRequest req, CancellationToken ct)
    {
        var userId = User.UserId();
        var entry = await db.PortfolioEntries.SingleOrDefaultAsync(e => e.Id == id && e.UserId == userId, ct);
        if (entry is null) return NotFound();

        var (png, problem) = PortfolioImages.Read(req.DataUrl);
        if (png is null) return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]> { ["dataUrl"] = [problem!] }));

        entry.ScreenshotPng = png;
        entry.ScreenshotTakenAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return (await EntriesFor(userId, ct)).Single(e => e.Id == id);
    }

    [HttpGet("portfolio/{id:guid}/screenshot")]
    public async Task<IActionResult> ScreenshotImage(Guid id, CancellationToken ct)
    {
        var entry = await db.PortfolioEntries.AsNoTracking()
            .Where(e => e.Id == id)
            .Select(e => new { e.UserId, e.ScreenshotPng })
            .SingleOrDefaultAsync(ct);
        if (entry?.ScreenshotPng is null || !await CanSee(entry.UserId, ct)) return NotFound();

        Response.Headers.CacheControl = "private, max-age=86400"; // the URL changes with the picture
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(entry.ScreenshotPng, "image/png");
    }

    /// <summary>The project's code (the passing submission's files), for Download project.</summary>
    [HttpGet("portfolio/{id:guid}/files")]
    public async Task<ActionResult<PortfolioFilesResponse>> Files(Guid id, CancellationToken ct)
    {
        var entry = await db.PortfolioEntries.AsNoTracking()
            .Where(e => e.Id == id)
            .Select(e => new { e.UserId, e.Title, e.Submission.FilesJson })
            .SingleOrDefaultAsync(ct);
        if (entry is null || !await CanSee(entry.UserId, ct)) return NotFound();

        var files = JsonSerializer.Deserialize<Dictionary<string, string>>(entry.FilesJson, Json) ?? [];
        return new PortfolioFilesResponse(entry.Title, files);
    }

    /// <summary>The learner themselves, or a parent linked to them.</summary>
    private async Task<bool> CanSee(Guid ownerId, CancellationToken ct)
    {
        var me = User.UserId();
        return me == ownerId || (User.IsInRole(Roles.Parent) && await consent.IsParentOfAsync(me, ownerId, ct));
    }

    private async Task<List<PortfolioEntryDto>> EntriesFor(Guid userId, CancellationToken ct)
    {
        var rows = await db.PortfolioEntries.AsNoTracking()
            .Where(e => e.UserId == userId)
            .OrderByDescending(e => e.CreatedAt)
            .Select(e => new
            {
                e.Id, e.Title, e.Theme, CourseTitle = e.Challenge.Lesson.Course.Title, e.Challenge.LessonId,
                LessonTitle = e.Challenge.Lesson.Title, e.CreatedAt, e.UpdatedAt, e.ScreenshotTakenAt,
            })
            .ToListAsync(ct);

        return rows.Select(e => new PortfolioEntryDto(
            e.Id, e.Title, e.Theme, e.CourseTitle, e.LessonId, e.LessonTitle, e.CreatedAt, e.UpdatedAt,
            e.ScreenshotTakenAt is { } at ? $"/api/portfolio/{e.Id}/screenshot?v={at.Ticks}" : null)).ToList();
    }
}
