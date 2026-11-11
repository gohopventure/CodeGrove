using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Codegrove.Api.Data;
using Codegrove.Api.Data.Entities;
using Codegrove.Api.Services.Email;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Codegrove.Api.Services;

public sealed class AppOptions
{
    public string Name { get; set; } = "Codegrove";
    /// <summary>Public URL of the SPA, used to build links in emails.</summary>
    public string BaseUrl { get; set; } = "http://localhost:5173";
    /// <summary>Origin that serves runner.html, where learner code runs. Must differ from the app's origin.</summary>
    public string PreviewOrigin { get; set; } = "http://127.0.0.1:5173";
    /// <summary>Origin of the media CDN (lesson images, captions).</summary>
    public string MediaOrigin { get; set; } = "";
    /// <summary>Bump whenever the direct notice to parents changes; stored with each consent.</summary>
    public string ConsentNoticeVersion { get; set; } = "2026-11";
    public int ConsentTokenDays { get; set; } = 14;
}

public sealed record CreateChildCommand(string UserName, string Password, int BirthYear, int BirthMonth, AgeBand? AgeBand);

public sealed record CreateChildResult(
    bool Succeeded,
    IReadOnlyDictionary<string, string[]> Errors,
    AppUser? Child,
    bool ConsentRequired,
    string? ConfirmUrl);

/// <summary>
/// Creates child accounts and runs verifiable parental consent ("email plus").
/// Email plus is acceptable because children's data is only used internally. If a feature ever
/// discloses data to a third party (the AI tutor), it must use a stronger method and the separate
/// <see cref="ParentalConsent.AllowThirdPartyDisclosure"/> flag.
/// </summary>
public sealed class ConsentService(
    AppDbContext db,
    UserManager<AppUser> users,
    IContentFilter filter,
    IEmailSender email,
    AppOptions app,
    TimeProvider clock,
    ILogger<ConsentService> logger)
{
    public async Task<CreateChildResult> CreateChildAsync(AppUser parent, CreateChildCommand cmd, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);
        var errors = new Dictionary<string, string[]>();

        var nameCheck = filter.CheckUsername(cmd.UserName);
        if (!nameCheck.Ok) errors["userName"] = [nameCheck.Reason!];

        if (!AgePolicy.IsValidBirthDate(cmd.BirthYear, cmd.BirthMonth, today))
            errors["birthYear"] = ["Choose a birth month and year."];
        else
        {
            var age = AgePolicy.AgeInYears(cmd.BirthYear, cmd.BirthMonth, today);
            if (age < AgePolicy.MinimumLearnerAge) errors["birthYear"] = [$"Learners need to be at least {AgePolicy.MinimumLearnerAge}."];
            if (age >= 18) errors["birthYear"] = ["Adults can sign up for their own account."];
        }

        if (errors.Count > 0) return Failed(errors);

        var under13 = AgePolicy.IsUnder13(cmd.BirthYear, cmd.BirthMonth, today);
        var child = new AppUser
        {
            Id = Guid.NewGuid(),
            UserName = cmd.UserName,
            Email = null, // never collect a child's email
            AgeBand = cmd.AgeBand is AgeBand.Explorer or AgeBand.Builder
                ? cmd.AgeBand.Value
                : AgePolicy.DefaultBand(cmd.BirthYear, cmd.BirthMonth, today),
            BirthYear = (short)cmd.BirthYear,
            BirthMonth = (byte)cmd.BirthMonth,
            Status = under13 ? AccountStatus.PendingConsent : AccountStatus.Active,
            CreatedAt = now,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var created = await users.CreateAsync(child, cmd.Password);
        if (!created.Succeeded) return Failed(IdentityErrors(created));
        await users.AddToRoleAsync(child, Roles.Learner);

        db.ParentChildLinks.Add(new ParentChildLink { ParentUserId = parent.Id, ChildUserId = child.Id, CreatedAt = now });
        db.LearnerSettings.Add(new LearnerSettings { UserId = child.Id });

        string? confirmUrl = null;
        if (under13)
        {
            var token = NewToken();
            db.ParentalConsents.Add(new ParentalConsent
            {
                Id = Guid.NewGuid(),
                ParentUserId = parent.Id,
                ChildUserId = child.Id,
                Method = ConsentMethod.EmailPlus,
                Status = ConsentStatus.Pending,
                NoticeVersion = app.ConsentNoticeVersion,
                TokenHash = Hash(token),
                TokenExpiresAt = now.AddDays(app.ConsentTokenDays),
                CreatedAt = now,
            });
            confirmUrl = $"{app.BaseUrl.TrimEnd('/')}/consent/confirm?token={Uri.EscapeDataString(token)}";
        }

        Audit(parent.Id, "child.created", child.Id, new { under13, child.AgeBand });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        if (confirmUrl is not null)
            await SendConsentEmailAsync(parent.Email!, child.UserName!, confirmUrl, ct);

        return new CreateChildResult(true, new Dictionary<string, string[]>(), child, under13, confirmUrl);
    }

    /// <summary>
    /// The "plus" step: the signed-in parent confirms with the emailed token.
    /// The token must belong to a pending consent requested by this same parent.
    /// </summary>
    public async Task<bool> ConfirmAsync(Guid parentId, string token, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var hash = Hash(token);
        var consent = await db.ParentalConsents
            .Include(c => c.Child)
            .SingleOrDefaultAsync(c => c.TokenHash == hash && c.ParentUserId == parentId && c.Status == ConsentStatus.Pending, ct);

        if (consent is null || consent.TokenExpiresAt < now) return false;

        consent.Status = ConsentStatus.Granted;
        consent.GrantedAt = now;
        consent.TokenHash = null; // single use
        consent.Child.Status = AccountStatus.Active;

        Audit(parentId, "consent.granted", consent.ChildUserId, new { consent.Method, consent.NoticeVersion });
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Revokes consent: the child is suspended at once (the security stamp change signs them out
    /// within a minute) and the cleanup job deletes their data after a 7-day grace period.
    /// </summary>
    public async Task RevokeAsync(Guid parentId, Guid childId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var child = await db.Users.SingleAsync(u => u.Id == childId, ct);

        var active = await db.ParentalConsents
            .Where(c => c.ChildUserId == childId && (c.Status == ConsentStatus.Granted || c.Status == ConsentStatus.Pending))
            .ToListAsync(ct);
        foreach (var c in active)
        {
            c.Status = ConsentStatus.Revoked;
            c.RevokedAt = now;
            c.TokenHash = null;
        }

        // Teens (13+) have no consent row; revoking still suspends the linked account.
        if (active.Count == 0)
        {
            db.ParentalConsents.Add(new ParentalConsent
            {
                Id = Guid.NewGuid(), ParentUserId = parentId, ChildUserId = childId,
                Method = ConsentMethod.EmailPlus, Status = ConsentStatus.Revoked,
                NoticeVersion = app.ConsentNoticeVersion, CreatedAt = now, RevokedAt = now,
            });
        }

        child.Status = AccountStatus.Suspended;
        Audit(parentId, "consent.revoked", childId, null);
        await db.SaveChangesAsync(ct);
        await users.UpdateSecurityStampAsync(child);
    }

    /// <summary>Deletes a child and all their data. Links and consents are removed explicitly (Restrict FKs).</summary>
    public async Task DeleteChildAsync(Guid actorId, Guid childId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await db.ParentChildLinks.Where(l => l.ChildUserId == childId).ExecuteDeleteAsync(ct);
        await db.ParentalConsents.Where(c => c.ChildUserId == childId).ExecuteDeleteAsync(ct);

        var child = await db.Users.SingleOrDefaultAsync(u => u.Id == childId, ct);
        if (child is not null)
        {
            var result = await users.DeleteAsync(child); // cascades to progress, submissions, XP, badges
            if (!result.Succeeded) throw new InvalidOperationException("Could not delete the child account.");
        }

        Audit(actorId, "child.deleted", childId, null);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public Task<bool> IsParentOfAsync(Guid parentId, Guid childId, CancellationToken ct = default) =>
        db.ParentChildLinks.AnyAsync(l => l.ParentUserId == parentId && l.ChildUserId == childId, ct);

    // ----------------------------------------------------------------------------------------

    private async Task SendConsentEmailAsync(string parentEmail, string childUserName, string confirmUrl, CancellationToken ct)
    {
        var name = app.Name;
        var subject = $"Please confirm {childUserName}'s {name} account";
        var text =
            $"""
            You asked to create a {name} account with the username "{childUserName}".

            Before your child can sign in, please read what we collect and confirm:
            {confirmUrl}

            What we collect: the username and password you chose, birth month and year, and learning
            progress (lessons, code, quiz answers, time spent). We never ask your child for an email,
            real name, photo or location, we show no ads, and we share nothing with third parties.

            You can review, download or delete your child's data, or withdraw consent, at any time from
            your parent dashboard. If you didn't ask for this, ignore this email and the account will be
            deleted in {app.ConsentTokenDays} days.
            """;
        var html = "<pre style=\"font-family:system-ui,sans-serif;white-space:pre-wrap\">"
                   + WebUtility.HtmlEncode(text) + "</pre>";

        try
        {
            await email.SendAsync(parentEmail, subject, html, text, ct);
        }
        catch (Exception ex)
        {
            // The account stays pending; the parent can resend from the dashboard.
            logger.LogError(ex, "Consent email failed");
        }
    }

    private void Audit(Guid actorId, string action, Guid targetId, object? details) =>
        db.AuditLog.Add(new AuditLogEntry
        {
            ActorUserId = actorId,
            Action = action,
            TargetType = "User",
            TargetId = targetId.ToString(),
            CreatedAt = clock.GetUtcNow().UtcDateTime,
            DetailsJson = details is null ? null : JsonSerializer.Serialize(details),
        });

    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static CreateChildResult Failed(Dictionary<string, string[]> errors) =>
        new(false, errors, null, false, null);

    internal static Dictionary<string, string[]> IdentityErrors(IdentityResult result) =>
        result.Errors
            .GroupBy(e => e.Code.Contains("UserName", StringComparison.OrdinalIgnoreCase) ? "userName"
                : e.Code.Contains("Password", StringComparison.OrdinalIgnoreCase) ? "password" : "general")
            .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray());
}
