using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Microsoft.AspNetCore.Http
{
    /// <summary>Ready-made results: <c>return Results.NotFound();</c>. Each returns IResult.</summary>
    public static class Results
    {
        public static IResult Ok(object? value = null) => new Ok<object?>(value);
        public static IResult Created(string? uri, object? value) => new Created<object?>(uri, value);
        public static IResult Created() => new Created(null);
        public static IResult Accepted(string? uri = null, object? value = null) => new Accepted<object?>(uri, value);
        public static IResult NoContent() => new NoContent();
        public static IResult NotFound(object? value = null) => value is null ? new NotFound() : new NotFound<object?>(value);
        public static IResult BadRequest(object? error = null) => error is null ? new BadRequest() : new BadRequest<object?>(error);
        public static IResult Conflict(object? error = null) => error is null ? new Conflict() : new Conflict<object?>(error);
        public static IResult UnprocessableEntity(object? error = null) => error is null ? new UnprocessableEntity() : new UnprocessableEntity<object?>(error);
        public static IResult Unauthorized() => new UnauthorizedHttpResult();
        public static IResult StatusCode(int statusCode) => new StatusCodeHttpResult(statusCode);
        public static IResult Text(string? content, string? contentType = null, int? statusCode = null) => new ContentHttpResult(content, contentType, statusCode);
        public static IResult Content(string? content, string? contentType = null, int? statusCode = null) => new ContentHttpResult(content, contentType, statusCode);
        public static IResult Json(object? data, JsonSerializerOptions? options = null, string? contentType = null, int? statusCode = null) =>
            new JsonHttpResult<object?>(data, statusCode, contentType);

        public static IResult Problem(string? detail = null, string? instance = null, int? statusCode = null, string? title = null, string? type = null) =>
            new ProblemHttpResult(new ProblemDetails { Detail = detail, Instance = instance, Status = statusCode, Title = title, Type = type });

        public static IResult Problem(ProblemDetails problemDetails) => new ProblemHttpResult(problemDetails);

        public static IResult ValidationProblem(IDictionary<string, string[]> errors, string? detail = null, string? instance = null, int? statusCode = null, string? title = null, string? type = null) =>
            new ProblemHttpResult(new HttpValidationProblemDetails(errors) { Detail = detail, Instance = instance, Status = statusCode ?? 400, Title = title ?? "One or more validation errors occurred.", Type = type });
    }

    /// <summary>The same results with exact types, for <c>Results&lt;Ok&lt;Book&gt;, NotFound&gt;</c> return types.</summary>
    public static class TypedResults
    {
        public static Ok Ok() => new();
        public static Ok<TValue> Ok<TValue>(TValue? value) => new(value);
        public static Created Created(string? uri = null) => new(uri);
        public static Created<TValue> Created<TValue>(string? uri, TValue? value) => new(uri, value);
        public static Accepted<TValue> Accepted<TValue>(string? uri, TValue? value) => new(uri, value);
        public static NoContent NoContent() => new();
        public static NotFound NotFound() => new();
        public static NotFound<TValue> NotFound<TValue>(TValue? value) => new(value);
        public static BadRequest BadRequest() => new();
        public static BadRequest<TValue> BadRequest<TValue>(TValue? error) => new(error);
        public static Conflict Conflict() => new();
        public static Conflict<TValue> Conflict<TValue>(TValue? error) => new(error);
        public static UnprocessableEntity UnprocessableEntity() => new();
        public static UnauthorizedHttpResult Unauthorized() => new();
        public static StatusCodeHttpResult StatusCode(int statusCode) => new(statusCode);
        public static ContentHttpResult Text(string? content, string? contentType = null, int? statusCode = null) => new(content, contentType, statusCode);
        public static JsonHttpResult<TValue> Json<TValue>(TValue? data, int? statusCode = null, string? contentType = null) => new(data, statusCode, contentType);

        public static ProblemHttpResult Problem(string? detail = null, string? instance = null, int? statusCode = null, string? title = null, string? type = null) =>
            new(new ProblemDetails { Detail = detail, Instance = instance, Status = statusCode, Title = title, Type = type });

        public static ValidationProblem ValidationProblem(IDictionary<string, string[]> errors, string? detail = null, string? instance = null, string? title = null, string? type = null) =>
            new(new HttpValidationProblemDetails(errors) { Detail = detail, Instance = instance, Status = 400, Title = title ?? "One or more validation errors occurred.", Type = type });
    }
}

namespace Microsoft.AspNetCore.Http.HttpResults
{
    /// <summary>How every built-in result writes itself: a status, maybe a Location header, maybe a JSON body.</summary>
    internal static class Write
    {
        internal static Task Status(HttpContext context, int status)
        {
            context.Response.StatusCode = status;
            return Task.CompletedTask;
        }

        internal static Task Json(HttpContext context, int status, object? value, string? contentType = null, string? location = null)
        {
            context.Response.StatusCode = status;
            if (location is not null) context.Response.Headers["Location"] = location;
            context.Response.ContentType = contentType ?? "application/json; charset=utf-8";
            return context.Response.WriteAsync(Codegrove.WebShim.ShimJson.Serialize(value));
        }

        /// <summary>Ok(null) writes no body, like ASP.NET Core; Ok(value) writes JSON.</summary>
        internal static Task MaybeJson(HttpContext context, int status, object? value, string? location = null)
        {
            if (value is null)
            {
                context.Response.StatusCode = status;
                if (location is not null) context.Response.Headers["Location"] = location;
                return Task.CompletedTask;
            }
            return Json(context, status, value, location: location);
        }
    }

    public sealed class Ok : IResult, IStatusCodeHttpResult
    {
        public int? StatusCode => 200;
        public Task ExecuteAsync(HttpContext httpContext) => Write.Status(httpContext, 200);
    }

    public sealed class Ok<TValue>(TValue? value) : IResult, IStatusCodeHttpResult, IValueHttpResult
    {
        public TValue? Value { get; } = value;
        object? IValueHttpResult.Value => Value;
        public int? StatusCode => 200;
        public Task ExecuteAsync(HttpContext httpContext) => Write.MaybeJson(httpContext, 200, Value);
    }

    public sealed class Created(string? location) : IResult, IStatusCodeHttpResult
    {
        public string? Location { get; } = location;
        public int? StatusCode => 201;
        public Task ExecuteAsync(HttpContext httpContext) => Write.MaybeJson(httpContext, 201, null, Location);
    }

    public sealed class Created<TValue>(string? location, TValue? value) : IResult, IStatusCodeHttpResult, IValueHttpResult
    {
        public string? Location { get; } = location;
        public TValue? Value { get; } = value;
        object? IValueHttpResult.Value => Value;
        public int? StatusCode => 201;
        public Task ExecuteAsync(HttpContext httpContext) => Write.MaybeJson(httpContext, 201, Value, Location);
    }

    public sealed class Accepted<TValue>(string? location, TValue? value) : IResult, IStatusCodeHttpResult, IValueHttpResult
    {
        public string? Location { get; } = location;
        public TValue? Value { get; } = value;
        object? IValueHttpResult.Value => Value;
        public int? StatusCode => 202;
        public Task ExecuteAsync(HttpContext httpContext) => Write.MaybeJson(httpContext, 202, Value, Location);
    }

    public sealed class NoContent : IResult, IStatusCodeHttpResult
    {
        public int? StatusCode => 204;
        public Task ExecuteAsync(HttpContext httpContext) => Write.Status(httpContext, 204);
    }

    public sealed class NotFound : IResult, IStatusCodeHttpResult
    {
        public int? StatusCode => 404;
        public Task ExecuteAsync(HttpContext httpContext) => Write.Status(httpContext, 404);
    }

    public sealed class NotFound<TValue>(TValue? value) : IResult, IStatusCodeHttpResult, IValueHttpResult
    {
        public TValue? Value { get; } = value;
        object? IValueHttpResult.Value => Value;
        public int? StatusCode => 404;
        public Task ExecuteAsync(HttpContext httpContext) => Write.MaybeJson(httpContext, 404, Value);
    }

    public sealed class BadRequest : IResult, IStatusCodeHttpResult
    {
        public int? StatusCode => 400;
        public Task ExecuteAsync(HttpContext httpContext) => Write.Status(httpContext, 400);
    }

    public sealed class BadRequest<TValue>(TValue? value) : IResult, IStatusCodeHttpResult, IValueHttpResult
    {
        public TValue? Value { get; } = value;
        object? IValueHttpResult.Value => Value;
        public int? StatusCode => 400;
        public Task ExecuteAsync(HttpContext httpContext) => Write.MaybeJson(httpContext, 400, Value);
    }

    public sealed class Conflict : IResult, IStatusCodeHttpResult
    {
        public int? StatusCode => 409;
        public Task ExecuteAsync(HttpContext httpContext) => Write.Status(httpContext, 409);
    }

    public sealed class Conflict<TValue>(TValue? value) : IResult, IStatusCodeHttpResult, IValueHttpResult
    {
        public TValue? Value { get; } = value;
        object? IValueHttpResult.Value => Value;
        public int? StatusCode => 409;
        public Task ExecuteAsync(HttpContext httpContext) => Write.MaybeJson(httpContext, 409, Value);
    }

    public sealed class UnprocessableEntity : IResult, IStatusCodeHttpResult
    {
        public int? StatusCode => 422;
        public Task ExecuteAsync(HttpContext httpContext) => Write.Status(httpContext, 422);
    }

    public sealed class UnprocessableEntity<TValue>(TValue? value) : IResult, IStatusCodeHttpResult, IValueHttpResult
    {
        public TValue? Value { get; } = value;
        object? IValueHttpResult.Value => Value;
        public int? StatusCode => 422;
        public Task ExecuteAsync(HttpContext httpContext) => Write.MaybeJson(httpContext, 422, Value);
    }

    public sealed class UnauthorizedHttpResult : IResult, IStatusCodeHttpResult
    {
        public int? StatusCode => 401;
        public Task ExecuteAsync(HttpContext httpContext) => Write.Status(httpContext, 401);
    }

    public sealed class StatusCodeHttpResult(int statusCode) : IResult, IStatusCodeHttpResult
    {
        public int? StatusCode { get; } = statusCode;
        public Task ExecuteAsync(HttpContext httpContext) => Write.Status(httpContext, StatusCode!.Value);
    }

    public sealed class ContentHttpResult(string? content, string? contentType, int? statusCode) : IResult, IStatusCodeHttpResult
    {
        public string? ResponseContent { get; } = content;
        public string? ContentType { get; } = contentType;
        public int? StatusCode { get; } = statusCode;

        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCode ?? 200;
            httpContext.Response.ContentType = ContentType ?? "text/plain; charset=utf-8";
            return httpContext.Response.WriteAsync(ResponseContent ?? "");
        }
    }

    public sealed class JsonHttpResult<TValue>(TValue? value, int? statusCode, string? contentType) : IResult, IStatusCodeHttpResult, IValueHttpResult
    {
        public TValue? Value { get; } = value;
        object? IValueHttpResult.Value => Value;
        public int? StatusCode { get; } = statusCode;
        public string? ContentType { get; } = contentType;
        public Task ExecuteAsync(HttpContext httpContext) => Write.Json(httpContext, StatusCode ?? 200, Value, ContentType);
    }

    /// <summary>Writes ProblemDetails as application/problem+json, filling in type and title from the status like ASP.NET Core.</summary>
    public sealed class ProblemHttpResult : IResult, IStatusCodeHttpResult, IValueHttpResult
    {
        internal ProblemHttpResult(ProblemDetails problemDetails)
        {
            problemDetails.Status ??= problemDetails is HttpValidationProblemDetails ? 400 : 500;
            var defaults = Codegrove.WebShim.ShimJson.ProblemDefaults(problemDetails.Status.Value);
            problemDetails.Type ??= defaults.Type;
            problemDetails.Title ??= defaults.Title;
            ProblemDetails = problemDetails;
        }

        public ProblemDetails ProblemDetails { get; }
        object? IValueHttpResult.Value => ProblemDetails;
        public int? StatusCode => ProblemDetails.Status;
        public Task ExecuteAsync(HttpContext httpContext) =>
            Write.Json(httpContext, ProblemDetails.Status!.Value, ProblemDetails, "application/problem+json");
    }

    public sealed class ValidationProblem : IResult, IStatusCodeHttpResult, IValueHttpResult
    {
        private readonly ProblemHttpResult _inner;

        internal ValidationProblem(HttpValidationProblemDetails problemDetails)
        {
            _inner = new ProblemHttpResult(problemDetails);
            ProblemDetails = problemDetails;
        }

        public HttpValidationProblemDetails ProblemDetails { get; }
        object? IValueHttpResult.Value => ProblemDetails;
        public int? StatusCode => 400;
        public Task ExecuteAsync(HttpContext httpContext) => _inner.ExecuteAsync(httpContext);
    }

    /// <summary>"One of these results": <c>Results&lt;Ok&lt;Book&gt;, NotFound&gt;</c>. Each choice converts to it automatically.</summary>
    public sealed class Results<TResult1, TResult2> : IResult
        where TResult1 : IResult where TResult2 : IResult
    {
        private Results(IResult result) => Result = result;
        public IResult Result { get; }
        public Task ExecuteAsync(HttpContext httpContext) => Result.ExecuteAsync(httpContext);
        public static implicit operator Results<TResult1, TResult2>(TResult1 result) => new(result);
        public static implicit operator Results<TResult1, TResult2>(TResult2 result) => new(result);
    }

    public sealed class Results<TResult1, TResult2, TResult3> : IResult
        where TResult1 : IResult where TResult2 : IResult where TResult3 : IResult
    {
        private Results(IResult result) => Result = result;
        public IResult Result { get; }
        public Task ExecuteAsync(HttpContext httpContext) => Result.ExecuteAsync(httpContext);
        public static implicit operator Results<TResult1, TResult2, TResult3>(TResult1 result) => new(result);
        public static implicit operator Results<TResult1, TResult2, TResult3>(TResult2 result) => new(result);
        public static implicit operator Results<TResult1, TResult2, TResult3>(TResult3 result) => new(result);
    }

    public sealed class Results<TResult1, TResult2, TResult3, TResult4> : IResult
        where TResult1 : IResult where TResult2 : IResult where TResult3 : IResult where TResult4 : IResult
    {
        private Results(IResult result) => Result = result;
        public IResult Result { get; }
        public Task ExecuteAsync(HttpContext httpContext) => Result.ExecuteAsync(httpContext);
        public static implicit operator Results<TResult1, TResult2, TResult3, TResult4>(TResult1 result) => new(result);
        public static implicit operator Results<TResult1, TResult2, TResult3, TResult4>(TResult2 result) => new(result);
        public static implicit operator Results<TResult1, TResult2, TResult3, TResult4>(TResult3 result) => new(result);
        public static implicit operator Results<TResult1, TResult2, TResult3, TResult4>(TResult4 result) => new(result);
    }
}
