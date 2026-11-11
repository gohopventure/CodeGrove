using System.ComponentModel.DataAnnotations;
using Codegrove.Api.Data;
using Codegrove.Api.Data.Entities;
using Codegrove.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Codegrove.Api.Features.Auth;

public sealed record RegisterParentRequest(
    [Required, EmailAddress, MaxLength(254)] string Email,
    [Required, MinLength(8), MaxLength(128)] string Password,
    bool ConfirmAdult);

public sealed record RegisterTeenRequest(
    [Required, MaxLength(20)] string UserName,
    [Required, EmailAddress, MaxLength(254)] string Email,
    [Required, MinLength(8), MaxLength(128)] string Password,
    int BirthYear,
    int BirthMonth);

public sealed record LoginRequest([Required, MaxLength(254)] string Login, [Required, MaxLength(128)] string Password);

public sealed record MeResponse(
    Guid Id, string UserName, IReadOnlyList<string> Roles, AgeBand AgeBand, AccountStatus Status,
    bool UseDyslexiaFont, bool ReducedMotion);

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    UserManager<AppUser> users,
    SignInManager<AppUser> signIn,
    IContentFilter filter,
    AppDbContext db,
    TimeProvider clock) : ControllerBase
{
    /// <summary>Cookie set when someone under 13 tries to self-register, so the age gate can't simply be retried.</summary>
    public const string AgeGateCookie = "cg.agegate";

    [HttpPost("register-parent")]
    [EnableRateLimiting(RateLimits.Auth)]
    public async Task<ActionResult<MeResponse>> RegisterParent(RegisterParentRequest req)
    {
        if (!req.ConfirmAdult)
            return ValidationError("confirmAdult", "Parent accounts are for adults (18+).");

        var email = req.Email.Trim();
        if (await users.FindByEmailAsync(email) is not null)
            return ValidationError("email", "An account with that email already exists. Try signing in.");

        var parent = new AppUser
        {
            Id = Guid.NewGuid(),
            UserName = email, // parents sign in with their email; their username is never shown to children
            Email = email,
            AgeBand = AgeBand.Adult,
            Status = AccountStatus.Active,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        };

        var result = await users.CreateAsync(parent, req.Password);
        if (!result.Succeeded) return ValidationErrors(ConsentService.IdentityErrors(result));

        await users.AddToRoleAsync(parent, Roles.Parent);
        await signIn.SignInAsync(parent, isPersistent: true);
        return await Me(parent);
    }

    /// <summary>
    /// Self-registration for 13+. Under-13 answers are turned away without storing anything,
    /// and a cookie blocks an immediate retry with a different birth date (neutral age screen).
    /// </summary>
    [HttpPost("register-teen")]
    [EnableRateLimiting(RateLimits.Auth)]
    public async Task<ActionResult<MeResponse>> RegisterTeen(RegisterTeenRequest req)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        if (Request.Cookies.ContainsKey(AgeGateCookie))
            return AskAGrownUp();

        if (!AgePolicy.IsValidBirthDate(req.BirthYear, req.BirthMonth, today))
            return ValidationError("birthYear", "Choose your birth month and year.");

        if (AgePolicy.IsUnder13(req.BirthYear, req.BirthMonth, today))
        {
            Response.Cookies.Append(AgeGateCookie, "1", new CookieOptions
            {
                HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = Request.IsHttps,
                MaxAge = TimeSpan.FromHours(24),
            });
            return AskAGrownUp();
        }

        var nameCheck = filter.CheckUsername(req.UserName);
        if (!nameCheck.Ok) return ValidationError("userName", nameCheck.Reason!);

        var email = req.Email.Trim();
        if (await users.FindByEmailAsync(email) is not null)
            return ValidationError("email", "An account with that email already exists.");

        var teen = new AppUser
        {
            Id = Guid.NewGuid(),
            UserName = req.UserName,
            Email = email,
            AgeBand = AgePolicy.DefaultBand(req.BirthYear, req.BirthMonth, today),
            BirthYear = (short)req.BirthYear,
            BirthMonth = (byte)req.BirthMonth,
            Status = AccountStatus.Active,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        };

        var result = await users.CreateAsync(teen, req.Password);
        if (!result.Succeeded) return ValidationErrors(ConsentService.IdentityErrors(result));

        await users.AddToRoleAsync(teen, Roles.Learner);
        db.LearnerSettings.Add(new LearnerSettings { UserId = teen.Id });
        await db.SaveChangesAsync();

        await signIn.SignInAsync(teen, isPersistent: true);
        return await Me(teen);
    }

    [HttpPost("login")]
    [EnableRateLimiting(RateLimits.Auth)]
    public async Task<ActionResult<MeResponse>> Login(LoginRequest req)
    {
        var login = req.Login.Trim();
        var user = await users.FindByNameAsync(login) ?? await users.FindByEmailAsync(login);

        // Same message for unknown user and wrong password, so usernames can't be probed.
        var invalid = Problem(statusCode: 401, title: "That username or password didn't work. Check them and try again.");
        if (user is null) return invalid;

        if (user.Status == AccountStatus.PendingConsent)
            return Problem(statusCode: 403, title: "Almost there! A grown-up needs to say yes in the email we sent them.");
        if (user.Status == AccountStatus.Suspended)
            return Problem(statusCode: 403, title: "This account is turned off. Ask your grown-up for help.");

        var result = await signIn.PasswordSignInAsync(user, req.Password, isPersistent: true, lockoutOnFailure: true);
        if (result.IsLockedOut)
            return Problem(statusCode: 423, title: "Too many tries. Take a 5-minute break, then try again.");
        if (!result.Succeeded) return invalid;

        return await Me(user);
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        await signIn.SignOutAsync();
        return NoContent();
    }

    /// <summary>Current user, or 204 when signed out (a normal state, so not an error).</summary>
    [HttpGet("me")]
    public async Task<ActionResult<MeResponse>> Me()
    {
        if (User.Identity?.IsAuthenticated != true) return NoContent();
        var user = await users.GetUserAsync(User);
        if (user is null) return NoContent();
        return await Me(user);
    }

    private async Task<MeResponse> Me(AppUser user) =>
        new(user.Id, user.UserName!, [.. await users.GetRolesAsync(user)], user.AgeBand, user.Status,
            user.UseDyslexiaFont, user.ReducedMotion);

    private ObjectResult AskAGrownUp() =>
        Problem(statusCode: 422, title: "Ask a parent or guardian to set up your account. They can create one for you in a few minutes.",
            type: "https://codegrove.example/problems/age-gate");

    private ActionResult ValidationError(string field, string message) =>
        ValidationErrors(new Dictionary<string, string[]> { [field] = [message] });

    private ActionResult ValidationErrors(IDictionary<string, string[]> errors) =>
        ValidationProblem(new ValidationProblemDetails(errors));
}
