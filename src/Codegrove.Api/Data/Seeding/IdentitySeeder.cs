using Codegrove.Api.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Codegrove.Api.Data.Seeding;

/// <summary>Creates roles everywhere, and demo accounts in Development only.</summary>
public sealed class IdentitySeeder(
    RoleManager<IdentityRole<Guid>> roles,
    UserManager<AppUser> users,
    AppDbContext db,
    TimeProvider clock)
{
    public const string DemoPassword = "codegrove-demo";

    public async Task SeedRolesAsync()
    {
        foreach (var role in Roles.All)
        {
            if (!await roles.RoleExistsAsync(role))
                await roles.CreateAsync(new IdentityRole<Guid>(role));
        }
    }

    /// <summary>
    /// Demo family for local testing: a parent, an Explorer child (consent already granted),
    /// a Builder teen and an admin. All share <see cref="DemoPassword"/>. Never runs outside Development.
    /// </summary>
    public async Task SeedDemoAccountsAsync()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var parent = await EnsureUser("parent@example.com", "parent@example.com", AgeBand.Adult, null, null, Roles.Parent);
        await EnsureUser("admin@example.com", "admin@example.com", AgeBand.Adult, null, null, Roles.Admin);

        var explorer = await EnsureUser("PixelFox42", null, AgeBand.Explorer, (short)(now.Year - 11), 3, Roles.Learner);
        var builder = await EnsureUser("ByteRunner", null, AgeBand.Builder, (short)(now.Year - 15), 6, Roles.Learner);

        foreach (var child in new[] { explorer, builder })
        {
            if (!await db.ParentChildLinks.AnyAsync(l => l.ChildUserId == child.Id))
                db.ParentChildLinks.Add(new ParentChildLink { ParentUserId = parent.Id, ChildUserId = child.Id, CreatedAt = now });
            if (!await db.LearnerSettings.AnyAsync(s => s.UserId == child.Id))
                db.LearnerSettings.Add(new LearnerSettings { UserId = child.Id });
        }

        if (!await db.ParentalConsents.AnyAsync(c => c.ChildUserId == explorer.Id))
        {
            db.ParentalConsents.Add(new ParentalConsent
            {
                Id = Guid.NewGuid(), ParentUserId = parent.Id, ChildUserId = explorer.Id,
                Method = ConsentMethod.EmailPlus, Status = ConsentStatus.Granted, NoticeVersion = "demo",
                CreatedAt = now, GrantedAt = now,
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task<AppUser> EnsureUser(string userName, string? email, AgeBand band, short? birthYear, byte? birthMonth, string role)
    {
        var user = await users.FindByNameAsync(userName);
        if (user is not null) return user;

        user = new AppUser
        {
            Id = Guid.NewGuid(), UserName = userName, Email = email, EmailConfirmed = email is not null,
            AgeBand = band, BirthYear = birthYear, BirthMonth = birthMonth,
            Status = AccountStatus.Active, CreatedAt = clock.GetUtcNow().UtcDateTime,
        };
        var result = await users.CreateAsync(user, DemoPassword);
        if (!result.Succeeded)
            throw new InvalidOperationException($"Demo user {userName}: {string.Join(", ", result.Errors.Select(e => e.Description))}");
        await users.AddToRoleAsync(user, role);
        return user;
    }
}
