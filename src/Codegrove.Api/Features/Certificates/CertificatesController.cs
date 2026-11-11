using Codegrove.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Codegrove.Api.Features.Certificates;

public sealed record CertificateDto(string Code, string UserName, string TrackTitle, DateTime IssuedAt);

/// <summary>
/// Public certificate verification. Shows only username, track and date: no age, no band,
/// nothing that identifies a child beyond the username they chose.
/// </summary>
[ApiController]
[Route("api/certificates")]
public sealed class CertificatesController(AppDbContext db) : ControllerBase
{
    [HttpGet("{code}")]
    public async Task<ActionResult<CertificateDto>> Verify(string code, CancellationToken ct)
    {
        var normalized = code.Trim().ToUpperInvariant();
        var cert = await db.Certificates.AsNoTracking()
            .Where(c => c.VerificationCode == normalized)
            .Select(c => new CertificateDto(c.VerificationCode, c.UserNameAtIssue, c.Track.Title, c.IssuedAt))
            .SingleOrDefaultAsync(ct);
        return cert is null ? NotFound() : cert;
    }
}
