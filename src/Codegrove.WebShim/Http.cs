using System.Text;

namespace Microsoft.AspNetCore.Http
{
    /// <summary>Anything an endpoint can return that knows how to write itself into the response.</summary>
    public interface IResult
    {
        Task ExecuteAsync(HttpContext httpContext);
    }

    /// <summary>A result that has a status code (all the built-in ones do).</summary>
    public interface IStatusCodeHttpResult
    {
        int? StatusCode { get; }
    }

    /// <summary>A result that carries a value (Ok&lt;T&gt;, Created&lt;T&gt; …).</summary>
    public interface IValueHttpResult
    {
        object? Value { get; }
    }

    /// <summary>Named status codes, as in ASP.NET Core (only the ones the lessons meet).</summary>
    public static class StatusCodes
    {
        public const int Status200OK = 200;
        public const int Status201Created = 201;
        public const int Status202Accepted = 202;
        public const int Status204NoContent = 204;
        public const int Status400BadRequest = 400;
        public const int Status401Unauthorized = 401;
        public const int Status403Forbidden = 403;
        public const int Status404NotFound = 404;
        public const int Status405MethodNotAllowed = 405;
        public const int Status409Conflict = 409;
        public const int Status415UnsupportedMediaType = 415;
        public const int Status422UnprocessableEntity = 422;
        public const int Status500InternalServerError = 500;
        public const int Status503ServiceUnavailable = 503;
    }

    /// <summary>The HTTP method names.</summary>
    public static class HttpMethods
    {
        public static readonly string Get = "GET";
        public static readonly string Post = "POST";
        public static readonly string Put = "PUT";
        public static readonly string Patch = "PATCH";
        public static readonly string Delete = "DELETE";
    }

    /// <summary>One request and its response, while the request is being handled.</summary>
    public sealed class HttpContext
    {
        internal HttpContext(HttpRequest request, IServiceProvider services)
        {
            Request = request;
            Response = new HttpResponse();
            RequestServices = services;
        }

        public HttpRequest Request { get; }
        public HttpResponse Response { get; }
        public IServiceProvider RequestServices { get; set; }
        public IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
    }

    public sealed class HttpRequest
    {
        internal HttpRequest(string method, string path, string queryString, IDictionary<string, string> query, string? body, IDictionary<string, string> headers)
        {
            Method = method;
            Path = path;
            QueryString = queryString;
            Query = new Dictionary<string, string>(query, StringComparer.OrdinalIgnoreCase);
            Body = body;
            Headers = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        }

        public string Method { get; }
        /// <summary>The path without the query string, like "/books/3". (A PathString in ASP.NET Core; a string here.)</summary>
        public string Path { get; }
        /// <summary>"?search=frog", or "" when there is none.</summary>
        public string QueryString { get; }
        public IReadOnlyDictionary<string, string> Query { get; }
        public IReadOnlyDictionary<string, string> Headers { get; }
        public string? ContentType => Headers.GetValueOrDefault("Content-Type");
        internal string? Body { get; }
    }

    public sealed class HttpResponse
    {
        private readonly StringBuilder _body = new();

        public int StatusCode { get; set; } = 200;
        public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string? ContentType
        {
            get => Headers.TryGetValue("Content-Type", out var v) ? v : null;
            set
            {
                if (value is null) Headers.Remove("Content-Type");
                else Headers["Content-Type"] = value;
            }
        }

        internal string Body => _body.ToString();
        internal bool HasStarted => _body.Length > 0;

        public Task WriteAsync(string text)
        {
            _body.Append(text);
            return Task.CompletedTask;
        }
    }

    /// <summary>ProblemDetails plus a list of problems for each field.</summary>
    public class HttpValidationProblemDetails : Microsoft.AspNetCore.Mvc.ProblemDetails
    {
        public HttpValidationProblemDetails() { }
        public HttpValidationProblemDetails(IDictionary<string, string[]> errors) => Errors = new Dictionary<string, string[]>(errors, StringComparer.Ordinal);
        public IDictionary<string, string[]> Errors { get; set; } = new Dictionary<string, string[]>(StringComparer.Ordinal);
    }

    /// <summary>Binds a parameter's properties from several places at once (route, query, body …).</summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class AsParametersAttribute : Attribute;
}

namespace Microsoft.AspNetCore.Mvc
{
    /// <summary>Read this parameter from the request body (JSON).</summary>
    [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
    public sealed class FromBodyAttribute : Attribute;

    /// <summary>Read this parameter from the route, like {id} in "/books/{id}".</summary>
    [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
    public sealed class FromRouteAttribute : Attribute
    {
        public string? Name { get; set; }
    }

    /// <summary>Read this parameter from the query string, like ?page=2.</summary>
    [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
    public sealed class FromQueryAttribute : Attribute
    {
        public string? Name { get; set; }
    }

    /// <summary>Read this parameter from a request header.</summary>
    [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
    public sealed class FromHeaderAttribute : Attribute
    {
        public string? Name { get; set; }
    }

    /// <summary>Get this parameter from the services registered in builder.Services.</summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class FromServicesAttribute : Attribute;

    /// <summary>The standard JSON shape for errors (RFC 9457), as ASP.NET Core writes it.</summary>
    public class ProblemDetails
    {
        public string? Type { get; set; }
        public string? Title { get; set; }
        public int? Status { get; set; }
        public string? Detail { get; set; }
        public string? Instance { get; set; }
    }
}
