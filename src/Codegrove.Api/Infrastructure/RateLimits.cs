using System.Threading.RateLimiting;

namespace Codegrove.Api;

public static class RateLimits
{
    public const string Auth = "auth";

    /// <summary>Auth endpoints: RateLimits:AuthPerMinute (default 10) requests per minute per client IP.</summary>
    public static IServiceCollection AddAppRateLimits(this IServiceCollection services, IConfiguration config)
    {
        var perMinute = config.GetValue("RateLimits:AuthPerMinute", 10);
        return services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(Auth, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
    }
}
