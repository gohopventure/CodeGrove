namespace Codegrove.Api.Infrastructure;

/// <summary>
/// CSRF defense in depth (the cookie is already SameSite=Strict): every state-changing /api request
/// must declare Content-Type: application/json. A cross-site HTML form can't send that header,
/// and a cross-site fetch that does triggers a CORS preflight, which this API never approves.
/// </summary>
public sealed class RequireJsonMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var isMutating = HttpMethods.IsPost(request.Method) || HttpMethods.IsPut(request.Method)
                         || HttpMethods.IsPatch(request.Method) || HttpMethods.IsDelete(request.Method);

        if (isMutating && request.Path.StartsWithSegments("/api") && !request.HasJsonContentType())
        {
            context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            return context.Response.WriteAsJsonAsync(new
            {
                title = "Requests that change data must use Content-Type: application/json.",
                status = 415,
            });
        }

        return next(context);
    }
}
