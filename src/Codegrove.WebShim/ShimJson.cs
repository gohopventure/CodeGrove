using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Codegrove.WebShim;

/// <summary>JSON the way ASP.NET Core writes and reads it: web defaults (camelCase, case-insensitive reading).</summary>
internal static class ShimJson
{
    /// <summary>
    /// Learner types are only known at run time, so this uses reflection (the browser build turns it on with
    /// JsonSerializerIsReflectionEnabledByDefault and keeps System.Text.Json whole).
    /// </summary>
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    internal static string Serialize(object? value) => value switch
    {
        // ASP.NET Core leaves null fields out of ProblemDetails, in this order.
        ProblemDetails p => JsonSerializer.Serialize(ProblemShape(p), Options),
        null => "null",
        _ => JsonSerializer.Serialize(value, value.GetType(), Options),
    };

    internal static object? Deserialize(string json, Type type) => JsonSerializer.Deserialize(json, type, Options);

    private static Dictionary<string, object?> ProblemShape(ProblemDetails p)
    {
        var shape = new Dictionary<string, object?>();
        if (p.Type is not null) shape["type"] = p.Type;
        if (p.Title is not null) shape["title"] = p.Title;
        if (p.Status is not null) shape["status"] = p.Status;
        if (p.Detail is not null) shape["detail"] = p.Detail;
        if (p.Instance is not null) shape["instance"] = p.Instance;
        if (p is HttpValidationProblemDetails v) shape["errors"] = v.Errors;
        return shape;
    }

    /// <summary>ProblemDetailsDefaults from ASP.NET Core (RFC 9110 links and titles).</summary>
    internal static (string? Type, string? Title) ProblemDefaults(int status) => status switch
    {
        400 => ("https://tools.ietf.org/html/rfc9110#section-15.5.1", "Bad Request"),
        401 => ("https://tools.ietf.org/html/rfc9110#section-15.5.2", "Unauthorized"),
        403 => ("https://tools.ietf.org/html/rfc9110#section-15.5.4", "Forbidden"),
        404 => ("https://tools.ietf.org/html/rfc9110#section-15.5.5", "Not Found"),
        405 => ("https://tools.ietf.org/html/rfc9110#section-15.5.6", "Method Not Allowed"),
        406 => ("https://tools.ietf.org/html/rfc9110#section-15.5.7", "Not Acceptable"),
        408 => ("https://tools.ietf.org/html/rfc9110#section-15.5.9", "Request Timeout"),
        409 => ("https://tools.ietf.org/html/rfc9110#section-15.5.10", "Conflict"),
        412 => ("https://tools.ietf.org/html/rfc9110#section-15.5.13", "Precondition Failed"),
        415 => ("https://tools.ietf.org/html/rfc9110#section-15.5.16", "Unsupported Media Type"),
        422 => ("https://tools.ietf.org/html/rfc9110#section-15.5.21", "Unprocessable Entity"),
        426 => ("https://tools.ietf.org/html/rfc9110#section-15.5.22", "Upgrade Required"),
        500 => ("https://tools.ietf.org/html/rfc9110#section-15.6.1", "An error occurred while processing your request."),
        502 => ("https://tools.ietf.org/html/rfc9110#section-15.6.3", "Bad Gateway"),
        503 => ("https://tools.ietf.org/html/rfc9110#section-15.6.4", "Service Unavailable"),
        504 => ("https://tools.ietf.org/html/rfc9110#section-15.6.5", "Gateway Timeout"),
        _ => (null, null),
    };

    /// <summary>"Not Found" for 404, for messages.</summary>
    internal static string Reason(int status) => status switch
    {
        200 => "OK", 201 => "Created", 202 => "Accepted", 204 => "No Content",
        301 => "Moved Permanently", 302 => "Found", 304 => "Not Modified",
        400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed",
        409 => "Conflict", 415 => "Unsupported Media Type", 422 => "Unprocessable Entity",
        500 => "Internal Server Error", 502 => "Bad Gateway", 503 => "Service Unavailable", 504 => "Gateway Timeout",
        _ => "",
    };
}
