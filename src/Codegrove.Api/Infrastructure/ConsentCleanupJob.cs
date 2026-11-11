using Codegrove.Api.Data;
using Codegrove.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Codegrove.Api.Infrastructure;

/// <summary>
/// Hourly job: deletes child accounts whose consent was never confirmed (token expired) and
/// accounts whose consent was revoked more than 7 days ago. Runs in-process for the MVP;
/// move to an Azure Function when there's more than one app instance.
/// </summary>
public sealed class ConsentCleanupJob(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ConsentCleanupJob> logger)
    : BackgroundService
{
    public static readonly TimeSpan RevokedGracePeriod = TimeSpan.FromDays(7);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Consent cleanup failed; will retry next hour");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var consent = scope.ServiceProvider.GetRequiredService<ConsentService>();
        var now = clock.GetUtcNow().UtcDateTime;
        var revokedBefore = now - RevokedGracePeriod;

        var expired = await db.ParentalConsents
            .Where(c => (c.Status == Data.ConsentStatus.Pending && c.TokenExpiresAt < now)
                        || (c.Status == Data.ConsentStatus.Revoked && c.RevokedAt < revokedBefore))
            .Select(c => new { c.ChildUserId, c.ParentUserId })
            .Distinct()
            .ToListAsync(ct);

        foreach (var item in expired)
            await consent.DeleteChildAsync(item.ParentUserId, item.ChildUserId, ct);

        if (expired.Count > 0) logger.LogInformation("Deleted {Count} child accounts after consent expiry or revocation", expired.Count);
        return expired.Count;
    }
}
