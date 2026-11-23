using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Codegrove.WebShim;

/// <summary>
/// Routing, parameter binding and result writing for one request, following ASP.NET Core's minimal API
/// rules (RequestDelegateFactory) for the cases the lessons use.
/// </summary>
internal static class RequestPipeline
{
    public static async Task<ShimResponse> HandleAsync(WebApplicationCore app, ShimRequest request)
    {
        var method = request.Method.ToUpperInvariant();
        var (path, queryString, query) = SplitPath(request.Path);
        var headers = new Dictionary<string, string>(request.Headers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        if (request.Body is { Length: > 0 } && !headers.ContainsKey("Content-Type")) headers["Content-Type"] = "application/json";

        using var scope = app.Services.CreateScope();
        var context = new HttpContext(new HttpRequest(method, path, queryString, query, request.Body, headers), scope.ServiceProvider);

        // ASP.NET Core builds every endpoint together, on the first request. If one can't be built, no request
        // gets through: each one fails with the first broken endpoint's error, whatever its path.
        foreach (var e in app.Endpoints)
        {
            try
            {
                CheckBodyParameters(e, app.Services);
            }
            catch (EndpointSetupException setup)
            {
                return Crashed(context, setup);
            }
        }

        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();

        // Routing: every endpoint whose route matches, then the most specific one for this method.
        var matches = app.Endpoints
            .Select(e => (Endpoint: e, Values: e.Pattern.Match(segments)))
            .Where(m => m.Values is not null)
            .ToList();
        if (matches.Count == 0) return Finish(context, 404);
        var forMethod = matches.Where(m => m.Endpoint.Methods.Contains(method)).ToList();
        if (forMethod.Count == 0)
        {
            context.Response.Headers["Allow"] = string.Join(", ", matches.SelectMany(m => m.Endpoint.Methods).Distinct());
            return Finish(context, 405);
        }
        var best = forMethod.OrderBy(m => m.Endpoint.Pattern.Precedence, PrecedenceComparer.Instance).ToList();
        if (best.Count > 1 && PrecedenceComparer.Instance.Compare(best[0].Endpoint.Pattern.Precedence, best[1].Endpoint.Pattern.Precedence) == 0)
        {
            var tied = best.Where(b => PrecedenceComparer.Instance.Compare(b.Endpoint.Pattern.Precedence, best[0].Endpoint.Pattern.Precedence) == 0);
            var error = new InvalidOperationException(
                "The request matched multiple endpoints. Matches: " + Environment.NewLine + string.Join(Environment.NewLine, tied.Select(t => t.Endpoint)));
            return Crashed(context, error);
        }
        var (endpoint, routeValues) = best[0];

        object?[] args;
        try
        {
            args = Bind(endpoint, routeValues!, context);
        }
        catch (BadRequestException bad)
        {
            // In Development, ASP.NET Core answers 400 and explains which parameter couldn't be read.
            context.Response.StatusCode = bad.StatusCode;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(bad.Message);
            return Finish(context, bad.StatusCode);
        }

        try
        {
            var returned = endpoint.Handler.Method.Invoke(endpoint.Handler.Target, args);
            var value = await Unwrap(returned, endpoint.Handler.Method.ReturnType);
            await WriteResult(context, value, endpoint.Handler.Method.ReturnType);
        }
        catch (Exception ex)
        {
            return Crashed(context, ex is TargetInvocationException { InnerException: { } inner } ? inner : ex);
        }
        return Finish(context, context.Response.StatusCode);
    }

    private static ShimResponse Finish(HttpContext context, int status)
    {
        context.Response.StatusCode = status;
        return new ShimResponse(status, context.Response.ContentType, context.Response.Body,
            new Dictionary<string, string>(context.Response.Headers, StringComparer.OrdinalIgnoreCase), null);
    }

    /// <summary>An exception in the endpoint: 500, like the developer exception page (which this mimics in plain text).</summary>
    private static ShimResponse Crashed(HttpContext context, Exception error)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = "text/plain; charset=utf-8" };
        // EndpointSetupException is the shim's own subtype; ASP.NET Core throws a plain InvalidOperationException.
        var type = error is EndpointSetupException ? typeof(InvalidOperationException) : error.GetType();
        return new ShimResponse(500, headers["Content-Type"], $"{type.FullName}: {error.Message}", headers, error);
    }

    private static (string Path, string QueryString, Dictionary<string, string> Query) SplitPath(string raw)
    {
        var at = raw.IndexOf('?');
        var path = at < 0 ? raw : raw[..at];
        var queryString = at < 0 ? "" : raw[at..];
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in queryString.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            // Repeated keys: ASP.NET Core keeps all of them; a single value is what these lessons read.
            query.TryAdd(key, value);
        }
        if (!path.StartsWith('/')) path = "/" + path;
        return (path, queryString, query);
    }

    // ---- Binding -------------------------------------------------------------------------------------

    /// <summary>
    /// An endpoint ASP.NET Core can't build. <see cref="Unplaced"/> names the parameter types it couldn't find a
    /// source for (the likely unregistered services); <see cref="BodyTypes"/> the one that did get the body.
    /// </summary>
    internal sealed class EndpointSetupException(Endpoint endpoint, List<string> bodyTypes, List<string> unplaced, bool noBodyMethod, string message)
        : InvalidOperationException(message)
    {
        /// <summary>"GET /pets"</summary>
        public string EndpointText { get; } = $"{string.Join("/", endpoint.Methods)} {endpoint.Pattern.Text}";
        public List<string> BodyTypes { get; } = bodyTypes;
        public List<string> Unplaced { get; } = unplaced;
        /// <summary>True: a GET/DELETE-style endpoint inferred a body. False: a second body parameter.</summary>
        public bool NoBodyMethod { get; } = noBodyMethod;
    }

    private sealed class BadRequestException(string message, int statusCode = 400) : Exception(message)
    {
        public int StatusCode { get; } = statusCode;
    }

    private static readonly NullabilityInfoContext s_nullability = new();

    private static object?[] Bind(Endpoint endpoint, Dictionary<string, string> routeValues, HttpContext context)
    {
        var parameters = endpoint.Handler.Method.GetParameters();
        var routeNames = new HashSet<string>(endpoint.Pattern.ParameterNames, StringComparer.OrdinalIgnoreCase);
        var services = (ShimServiceProvider)context.RequestServices;
        var args = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            var type = p.ParameterType;
            var name = p.Name ?? $"arg{i}";

            if (p.GetCustomAttribute<FromRouteAttribute>() is { } fromRoute) args[i] = FromText(p, routeValues.GetValueOrDefault(fromRoute.Name ?? name), "route");
            else if (p.GetCustomAttribute<FromQueryAttribute>() is { } fromQuery) args[i] = FromText(p, context.Request.Query.GetValueOrDefault(fromQuery.Name ?? name), "query string");
            else if (p.GetCustomAttribute<FromHeaderAttribute>() is { } fromHeader) args[i] = FromText(p, context.Request.Headers.GetValueOrDefault(fromHeader.Name ?? name), "header");
            else if (p.GetCustomAttribute<FromBodyAttribute>() is not null) args[i] = FromBody(p, context, implicitBody: false);
            else if (p.GetCustomAttribute<FromServicesAttribute>() is not null) args[i] = services.GetRequiredService(type);
            else if (p.GetCustomAttribute<AsParametersAttribute>() is not null)
                throw new NotSupportedException("[AsParameters] isn't supported by Codegrove's practice server yet.");
            else if (type == typeof(HttpContext)) args[i] = context;
            else if (type == typeof(HttpRequest)) args[i] = context.Request;
            else if (type == typeof(HttpResponse)) args[i] = context.Response;
            else if (type == typeof(CancellationToken)) args[i] = CancellationToken.None;
            else if (type == typeof(IServiceProvider)) args[i] = services;
            else if (routeNames.Contains(name)) args[i] = FromText(p, routeValues.GetValueOrDefault(name), "route");
            else if (services.IsService(type)) args[i] = services.GetService(type);
            else if (IsSimple(type)) args[i] = FromText(p, context.Request.Query.GetValueOrDefault(name), "query string");
            else args[i] = FromBody(p, context, implicitBody: true);
        }
        return args;
    }

    /// <summary>
    /// The endpoint-building rules RequestDelegateFactory enforces, with its messages (checked against
    /// ASP.NET Core 10 by tests/Codegrove.WebApi.ParityTests):
    /// - only one parameter can come from the body: the first [FromBody] or inferred one takes it, and any
    ///   later one is "UNKNOWN" ("Failure to infer one or more parameters…");
    /// - a GET, DELETE, HEAD or OPTIONS endpoint can't infer a body ("Body was inferred but the method does
    ///   not allow inferred body parameters…").
    /// The usual cause is a class the learner meant as a service but didn't register.
    /// </summary>
    private static void CheckBodyParameters(Endpoint endpoint, ShimServiceProvider services)
    {
        var parameters = endpoint.Handler.Method.GetParameters();
        var routeNames = new HashSet<string>(endpoint.Pattern.ParameterNames, StringComparer.OrdinalIgnoreCase);
        var rows = new List<(ParameterInfo P, string Source)>();
        var bodyTaken = false;
        foreach (var p in parameters)
        {
            var source = SourceOf(p, routeNames, services);
            if (source is "Body (Inferred)" or "Body (Attribute)")
            {
                if (bodyTaken) source = "UNKNOWN";
                bodyTaken = true;
            }
            rows.Add((p, source));
        }

        string Table() =>
            "Below is the list of parameters that we found: " + Environment.NewLine + Environment.NewLine +
            $"{"Parameter",-20}| {"Source",-30}" + Environment.NewLine + new string('-', 81) + Environment.NewLine +
            string.Join(Environment.NewLine, rows.Select(r => $"{r.P.Name,-20}| {r.Source,-15}".TrimEnd())) + Environment.NewLine + Environment.NewLine + Environment.NewLine;

        var unknown = rows.Where(r => r.Source == "UNKNOWN").ToList();
        if (unknown.Count > 0)
            throw new EndpointSetupException(endpoint, rows.Where(r => r.Source != "UNKNOWN" && r.Source.StartsWith("Body")).Select(r => TypeName(r.P.ParameterType)).ToList(),
                unknown.Select(r => TypeName(r.P.ParameterType)).ToList(), noBodyMethod: false,
                "Failure to infer one or more parameters." + Environment.NewLine + Table() +
                "Did you mean to register the \"UNKNOWN\" parameters as a Service?");

        var noBodyMethods = new[] { "GET", "DELETE", "HEAD", "OPTIONS" };
        var inferred = rows.Where(r => r.Source == "Body (Inferred)").ToList();
        if (inferred.Count > 0 && endpoint.Methods.All(m => noBodyMethods.Contains(m)))
            throw new EndpointSetupException(endpoint, [], inferred.Select(r => TypeName(r.P.ParameterType)).ToList(), noBodyMethod: true,
                "Body was inferred but the method does not allow inferred body parameters." + Environment.NewLine + Table() +
                "Did you mean to register the \"Body (Inferred)\" parameter(s) as a Service or apply the [FromServices] or [FromBody] attribute?");
    }

    /// <summary>Where a parameter's value comes from, in Bind's order, named as ASP.NET Core's table names them.</summary>
    private static string SourceOf(ParameterInfo p, HashSet<string> routeNames, ShimServiceProvider services)
    {
        var type = p.ParameterType;
        if (p.GetCustomAttribute<FromRouteAttribute>() is not null) return "Route (Attribute)";
        if (p.GetCustomAttribute<FromQueryAttribute>() is not null) return "Query (Attribute)";
        if (p.GetCustomAttribute<FromHeaderAttribute>() is not null) return "Header (Attribute)";
        if (p.GetCustomAttribute<FromBodyAttribute>() is not null) return "Body (Attribute)";
        if (p.GetCustomAttribute<FromServicesAttribute>() is not null) return "Services (Attribute)";
        if (type == typeof(HttpContext) || type == typeof(HttpRequest) || type == typeof(HttpResponse)
            || type == typeof(CancellationToken) || type == typeof(IServiceProvider)) return "Special";
        if (routeNames.Contains(p.Name ?? "")) return "Route (Inferred)";
        if (services.IsService(type)) return "Services (Inferred)";
        if (IsSimple(type)) return "Query String (Inferred)";
        return "Body (Inferred)";
    }

    /// <summary>Strings and anything with a TryParse (numbers, bool, Guid, dates, enums) come from the route or query string.</summary>
    private static bool IsSimple(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t == typeof(string) || t.IsEnum || FindTryParse(t) is not null;
    }

    private static MethodInfo? FindTryParse(Type t) =>
        t.GetMethod("TryParse", BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(IFormatProvider), t.MakeByRefType()])
        ?? t.GetMethod("TryParse", BindingFlags.Public | BindingFlags.Static, [typeof(string), t.MakeByRefType()]);

    private static object? FromText(ParameterInfo p, string? text, string source)
    {
        var type = p.ParameterType;
        if (text is null)
        {
            if (p.HasDefaultValue) return p.DefaultValue;
            if (IsOptional(p)) return null;
            throw new BadRequestException($"Required parameter \"{TypeName(type)} {p.Name}\" was not provided from {source}.");
        }

        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(string)) return text;
        if (t.IsEnum)
            return Enum.TryParse(t, text, ignoreCase: true, out var e) ? e : throw Unparsable(p, text);

        var tryParse = FindTryParse(t)!;
        var withProvider = tryParse.GetParameters().Length == 3;
        object?[] args = withProvider ? [text, CultureInfo.InvariantCulture, null] : [text, null];
        return tryParse.Invoke(null, args) is true ? args[^1] : throw Unparsable(p, text);
    }

    private static BadRequestException Unparsable(ParameterInfo p, string text) =>
        new($"Failed to bind parameter \"{TypeName(p.ParameterType)} {p.Name}\" from \"{text}\".");

    private static object? FromBody(ParameterInfo p, HttpContext context, bool implicitBody)
    {
        var body = context.Request.Body;
        if (string.IsNullOrWhiteSpace(body))
        {
            if (p.HasDefaultValue) return p.DefaultValue;
            if (IsOptional(p)) return null;
            throw new BadRequestException(implicitBody
                ? $"Implicit body inferred for parameter \"{p.Name}\" but no body was provided. Did you mean to use a Service instead?"
                : $"Required parameter \"{TypeName(p.ParameterType)} {p.Name}\" was not provided from body.");
        }
        var contentType = context.Request.ContentType ?? "";
        if (!contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            throw new BadRequestException($"Expected a supported JSON media type but got \"{contentType}\".", 415);
        try
        {
            return ShimJson.Deserialize(body, p.ParameterType)
                ?? throw new BadRequestException($"Required parameter \"{TypeName(p.ParameterType)} {p.Name}\" was not provided from body.");
        }
        catch (JsonException)
        {
            throw new BadRequestException($"Failed to read parameter \"{TypeName(p.ParameterType)} {p.Name}\" from the request body as JSON.");
        }
    }

    /// <summary>
    /// Nullable value types are optional; reference types are optional unless declared not-null. Learner code
    /// compiles with nullable off unless the file says #nullable enable, and then ASP.NET Core treats a plain
    /// <c>string</c> as optional too.
    /// </summary>
    private static bool IsOptional(ParameterInfo p)
    {
        if (Nullable.GetUnderlyingType(p.ParameterType) is not null) return true;
        if (p.ParameterType.IsValueType) return false;
        return s_nullability.Create(p).ReadState != NullabilityState.NotNull;
    }

    /// <summary>"int", "string", "Book": the short names ASP.NET Core uses in these messages.</summary>
    private static string TypeName(Type type) => Nullable.GetUnderlyingType(type) is { } inner ? $"Nullable<{TypeName(inner)}>" : type switch
    {
        _ when type == typeof(int) => "int",
        _ when type == typeof(long) => "long",
        _ when type == typeof(string) => "string",
        _ when type == typeof(bool) => "bool",
        _ when type == typeof(double) => "double",
        _ when type == typeof(decimal) => "decimal",
        _ when type == typeof(float) => "float",
        _ when type.IsGenericType => type.Name[..type.Name.IndexOf('`')] + "<" + string.Join(", ", type.GetGenericArguments().Select(TypeName)) + ">",
        _ => type.Name,
    };

    // ---- Results ---------------------------------------------------------------------------------------

    private static async Task<object?> Unwrap(object? returned, Type declared)
    {
        switch (returned)
        {
            case Task task:
                await task;
                return declared.IsGenericType ? task.GetType().GetProperty("Result")!.GetValue(task) : NoValue.Instance;
            case ValueTask vt:
                await vt;
                return NoValue.Instance;
            case not null when declared.IsGenericType && declared.GetGenericTypeDefinition() == typeof(ValueTask<>):
                var asTask = (Task)declared.GetMethod("AsTask")!.Invoke(returned, null)!;
                await asTask;
                return asTask.GetType().GetProperty("Result")!.GetValue(asTask);
            default:
                return declared == typeof(void) ? NoValue.Instance : returned;
        }
    }

    private sealed class NoValue
    {
        public static readonly NoValue Instance = new();
    }

    /// <summary>IResult writes itself; a string is text; void is an empty 200; anything else is JSON.</summary>
    private static Task WriteResult(HttpContext context, object? value, Type declared)
    {
        switch (value)
        {
            case NoValue:
                context.Response.StatusCode = 200;
                return Task.CompletedTask;
            case IResult result:
                return result.ExecuteAsync(context);
            case null when declared == typeof(string) || declared == typeof(Task<string>) || declared == typeof(ValueTask<string>):
                // ASP.NET Core passes the null straight to HttpResponse.WriteAsync(string), which throws (500).
                throw new ArgumentNullException("text");
            case string text:
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/plain; charset=utf-8";
                return context.Response.WriteAsync(text);
            default:
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json; charset=utf-8";
                return context.Response.WriteAsync(ShimJson.Serialize(value));
        }
    }

    private sealed class PrecedenceComparer : IComparer<int[]>
    {
        public static readonly PrecedenceComparer Instance = new();

        public int Compare(int[]? x, int[]? y)
        {
            for (var i = 0; i < Math.Min(x!.Length, y!.Length); i++)
                if (x[i] != y[i]) return x[i].CompareTo(y[i]);
            return 0;
        }
    }
}
