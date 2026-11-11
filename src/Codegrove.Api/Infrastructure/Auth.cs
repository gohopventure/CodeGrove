using System.Security.Claims;
using Codegrove.Api.Data;
using Codegrove.Api.Data.Entities;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Codegrove.Api;

public static class Roles
{
    public const string Learner = "Learner";
    public const string Parent = "Parent";
    public const string Teacher = "Teacher";
    public const string Admin = "Admin";

    public static readonly string[] All = [Learner, Parent, Teacher, Admin];
}

public static class Policies
{
    /// <summary>A learner whose account is Active (consent granted where required).</summary>
    public const string ActiveLearner = nameof(ActiveLearner);
    public const string Parent = nameof(Parent);
    public const string Admin = nameof(Admin);
}

public static class AppClaims
{
    public const string Status = "cg:status";
    public const string Band = "cg:band";
}

/// <summary>Adds account status and age band to the auth cookie so policies don't need a DB hit.</summary>
public sealed class AppClaimsPrincipalFactory(
    UserManager<AppUser> users, RoleManager<IdentityRole<Guid>> roles, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<AppUser, IdentityRole<Guid>>(users, roles, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(AppClaims.Status, user.Status.ToString()));
        identity.AddClaim(new Claim(AppClaims.Band, user.AgeBand.ToString()));
        return identity;
    }
}

public static class ClaimsPrincipalExtensions
{
    public static Guid UserId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)
                   ?? throw new InvalidOperationException("No user id claim."));

    public static AgeBand Band(this ClaimsPrincipal user) =>
        Enum.TryParse<AgeBand>(user.FindFirstValue(AppClaims.Band), out var band) ? band : AgeBand.Builder;
}

public static class AuthSetup
{
    public static IServiceCollection AddAppAuth(this IServiceCollection services, IWebHostEnvironment env)
    {
        services
            .AddIdentityCore<AppUser>(o =>
            {
                // NIST-style: length over complexity rules, which kids find confusing.
                o.Password.RequiredLength = 8;
                o.Password.RequireDigit = false;
                o.Password.RequireLowercase = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireNonAlphanumeric = false;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
                o.User.RequireUniqueEmail = false; // child accounts have no email; parent uniqueness is checked on sign-up
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager()
            .AddClaimsPrincipalFactory<AppClaimsPrincipalFactory>()
            .AddDefaultTokenProviders();

        // Revocation and suspensions take effect within a minute.
        services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));

        services
            .AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddIdentityCookies(cookies => cookies.ApplicationCookie?.Configure(o =>
            {
                o.Cookie.Name = "cg.auth";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Strict;
                o.Cookie.SecurePolicy = env.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                o.ExpireTimeSpan = TimeSpan.FromDays(14);
                o.SlidingExpiration = true;

                // An API never redirects to a login page; it answers 401/403.
                o.Events = new CookieAuthenticationEvents
                {
                    OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; },
                    OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; },
                    OnValidatePrincipal = SecurityStampValidator.ValidatePrincipalAsync,
                };
            }));

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.ActiveLearner, p => p
                .RequireRole(Roles.Learner)
                .RequireClaim(AppClaims.Status, nameof(AccountStatus.Active)))
            .AddPolicy(Policies.Parent, p => p.RequireRole(Roles.Parent))
            .AddPolicy(Policies.Admin, p => p.RequireRole(Roles.Admin));

        return services;
    }
}
