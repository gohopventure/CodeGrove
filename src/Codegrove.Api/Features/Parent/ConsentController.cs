using System.ComponentModel.DataAnnotations;
using Codegrove.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Codegrove.Api.Features.Parent;

public sealed record ConfirmConsentRequest([Required, MaxLength(100)] string Token, bool AgreeToNotice);

[ApiController]
[Route("api/consent")]
[Authorize(Policy = Policies.Parent)]
public sealed class ConsentController(ConsentService consent) : ControllerBase
{
    /// <summary>
    /// The "plus" step of email-plus consent. The parent opens the emailed link, signs in,
    /// reads the notice and ticks "I agree". The token must belong to this parent.
    /// </summary>
    [HttpPost("confirm")]
    public async Task<IActionResult> Confirm(ConfirmConsentRequest req, CancellationToken ct)
    {
        if (!req.AgreeToNotice)
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["agreeToNotice"] = ["Please read the notice and tick the box to give consent."],
            }));

        var ok = await consent.ConfirmAsync(User.UserId(), req.Token, ct);
        return ok
            ? NoContent()
            : Problem(statusCode: 400, title: "This link has expired or was already used. Add your child again from your dashboard to get a new one.");
    }
}
