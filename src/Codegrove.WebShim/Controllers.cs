using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Codegrove.WebShim;

/// <summary>One controller action, mapped as an endpoint by MapControllers.</summary>
internal sealed class ControllerAction(Type controller, MethodInfo method, bool isApiController)
{
    public Type Controller { get; } = controller;
    public MethodInfo Method { get; } = method;
    public bool IsApiController { get; } = isApiController;

    /// <summary>"Books" for BooksController.</summary>
    public string ControllerName => Controllers.NameOf(Controller);

    /// <summary>"GetById" for GetById and GetByIdAsync (MVC drops the Async suffix by default).</summary>
    public string ActionName => Method.Name.EndsWith("Async", StringComparison.Ordinal) && Method.Name.Length > 5 ? Method.Name[..^5] : Method.Name;
}

/// <summary>An action being run: what results need to write themselves (problem details, links).</summary>
internal sealed class ControllerRun(WebApplicationCore app, ControllerAction action)
{
    public bool IsApiController => action.IsApiController;

    /// <summary>
    /// The absolute URL of an action, as CreatedAtAction's Location: route values fill the template's
    /// parameters and the rest go in the query string. "No route matches the supplied values." otherwise.
    /// </summary>
    public string LinkTo(string? actionName, string? controllerName, object? routeValues)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (routeValues is not null)
            foreach (var p in routeValues.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                values[p.Name] = Convert.ToString(p.GetValue(routeValues), CultureInfo.InvariantCulture);

        var wantAction = actionName ?? action.ActionName;
        var wantController = controllerName ?? action.ControllerName;
        foreach (var e in app.Endpoints.Where(e => e.Action is { } a
            && a.ActionName.Equals(wantAction, StringComparison.OrdinalIgnoreCase)
            && a.ControllerName.Equals(wantController, StringComparison.OrdinalIgnoreCase)))
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var parts = new List<string>();
            var ok = true;
            foreach (var s in e.Pattern.Segments)
            {
                if (s.Literal is not null) { parts.Add(s.Literal); continue; }
                if (values.TryGetValue(s.Parameter!, out var v) && v is not null)
                {
                    parts.Add(Uri.EscapeDataString(v));
                    used.Add(s.Parameter!);
                }
                else if (!s.Optional && !s.CatchAll)
                {
                    ok = false;
                    break;
                }
            }
            if (!ok) continue;
            var query = string.Join("&", values.Where(v => !used.Contains(v.Key) && v.Value is not null)
                .Select(v => $"{Uri.EscapeDataString(v.Key)}={Uri.EscapeDataString(v.Value!)}"));
            return app.Url.TrimEnd('/') + "/" + string.Join("/", parts) + (query.Length > 0 ? "?" + query : "");
        }
        throw new InvalidOperationException("No route matches the supplied values.");
    }
}

/// <summary>
/// Controllers as ASP.NET Core MVC runs them, for the parts the lessons use: discovery (public classes
/// deriving from ControllerBase or named …Controller), attribute routing with [controller]/[action] tokens,
/// model binding, DataAnnotations validation with [ApiController]'s automatic 400, and results.
/// </summary>
internal static class Controllers
{
    public static string NameOf(Type controller) =>
        controller.Name.EndsWith("Controller", StringComparison.Ordinal) && controller.Name.Length > 10 ? controller.Name[..^10] : controller.Name;

    public static void Map(IEndpointRouteBuilder endpoints, Assembly learnerCode)
    {
        var sink = endpoints as IEndpointSink ?? throw new NotSupportedException("Codegrove's practice server can only map controllers on the app.");
        if (!sink.Core.Services.IsService(typeof(ControllersAdded)))
            throw new InvalidOperationException("Unable to find the required services. Please add all the required services by calling 'IServiceCollection.AddControllers' inside the call to 'ConfigureServices(...)' in the application startup code.");

        foreach (var type in learnerCode.GetTypes().Where(IsController).OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            var isApi = type.GetCustomAttribute<ApiControllerAttribute>(inherit: true) is not null
                || learnerCode.GetCustomAttribute<ApiControllerAttribute>() is not null;
            var classRoutes = type.GetCustomAttributes<RouteAttribute>(inherit: true).Select(r => r.Template).ToList();

            foreach (var method in Actions(type))
            {
                var action = new ControllerAction(type, method, isApi);
                var verbs = method.GetCustomAttributes<HttpMethodAttribute>(inherit: true).ToList();
                var methodRoutes = method.GetCustomAttributes<RouteAttribute>(inherit: true).Select(r => r.Template).ToList();

                // Each [Http…] attribute is a route; [Route] on the method adds routes for any method.
                var routes = new List<(string[] Methods, string? Template)>();
                routes.AddRange(verbs.Select(v => (v.HttpMethods.ToArray(), v.Template)));
                routes.AddRange(methodRoutes.Select(t => (verbs.SelectMany(v => v.HttpMethods).Distinct().ToArray(), (string?)t)));
                if (routes.Count == 0) routes.Add(([], null));

                foreach (var (methods, template) in routes)
                {
                    foreach (var full in Combine(classRoutes, template))
                    {
                        var text = ReplaceTokens(full, action);
                        sink.Core.Endpoints.Add(new Endpoint(methods, RoutePattern.Parse(text), null, action));
                    }
                }
            }
        }
    }

    private static bool IsController(Type t) =>
        t is { IsClass: true, IsAbstract: false, IsPublic: true, ContainsGenericParameters: false }
        && (typeof(ControllerBase).IsAssignableFrom(t) || t.Name.EndsWith("Controller", StringComparison.Ordinal));

    private static IEnumerable<MethodInfo> Actions(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.DeclaringType != typeof(object) && m.DeclaringType != typeof(ControllerBase)
                && !m.IsSpecialName && !m.IsGenericMethod
                && m.GetCustomAttribute<NonActionAttribute>(inherit: true) is null
                && m.GetBaseDefinition().DeclaringType != typeof(object));

    /// <summary>
    /// Attribute routes: a method template starting with / or ~/ stands alone; otherwise it goes after each
    /// controller template. No templates at all means the action isn't reachable (no conventional routes here).
    /// </summary>
    private static IEnumerable<string> Combine(List<string> classRoutes, string? methodTemplate)
    {
        if (methodTemplate is not null && (methodTemplate.StartsWith('/') || methodTemplate.StartsWith("~/", StringComparison.Ordinal)))
            return [methodTemplate.TrimStart('~')];
        if (classRoutes.Count == 0) return methodTemplate is null ? [] : [methodTemplate];
        return classRoutes.Select(c => methodTemplate is null or "" ? c : c.TrimEnd('/') + "/" + methodTemplate);
    }

    private static string ReplaceTokens(string template, ControllerAction action) =>
        template.Replace("[controller]", action.ControllerName, StringComparison.OrdinalIgnoreCase)
            .Replace("[action]", action.ActionName, StringComparison.OrdinalIgnoreCase);

    // ---- Running an action ------------------------------------------------------------------------------

    public static async Task InvokeAsync(WebApplicationCore app, Endpoint endpoint, Dictionary<string, string> routeValues, HttpContext context)
    {
        var action = endpoint.Action!;
        var run = new ControllerRun(app, action);
        var actionContext = new ActionContext(context, run);
        var services = (ShimServiceProvider)context.RequestServices;
        var modelState = new ModelStateDictionary();
        object?[] args;
        try
        {
            args = Bind(action, routeValues, context, services, modelState);
        }
        catch (UnsupportedMediaType)
        {
            // MVC has no input formatter for a body without a JSON Content-Type.
            await new StatusCodeResult(415).ExecuteResultAsync(actionContext);
            return;
        }

        if (action.IsApiController && !modelState.IsValid)
        {
            // [ApiController]: invalid input never reaches the action.
            await new ObjectResult(new ValidationProblemDetails(modelState)
            {
                Status = 400,
                Type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
            }) { StatusCode = 400 }.ExecuteResultAsync(actionContext);
            return;
        }

        var controller = services.CreateInstance(action.Controller);
        if (controller is ControllerBase cb)
        {
            cb.HttpContext = context;
            cb.UseModelState(modelState);
        }

        object? returned;
        try
        {
            returned = action.Method.Invoke(controller, args);
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw;
        }

        var declared = action.Method.ReturnType;
        var (value, valueType) = await Unwrap(returned, declared);
        await ToActionResult(value, valueType).ExecuteResultAsync(actionContext);
    }

    private static async Task<(object? Value, Type? Type)> Unwrap(object? returned, Type declared)
    {
        if (returned is Task task)
        {
            await task;
            if (!declared.IsGenericType) return (NoValue.Instance, null);
            return (task.GetType().GetProperty("Result")!.GetValue(task), declared.GetGenericArguments()[0]);
        }
        if (returned is ValueTask vt)
        {
            await vt;
            return (NoValue.Instance, null);
        }
        if (declared.IsGenericType && declared.GetGenericTypeDefinition() == typeof(ValueTask<>) && returned is not null)
        {
            var asTask = (Task)declared.GetMethod("AsTask")!.Invoke(returned, null)!;
            await asTask;
            return (asTask.GetType().GetProperty("Result")!.GetValue(asTask), declared.GetGenericArguments()[0]);
        }
        return declared == typeof(void) ? (NoValue.Instance, null) : (returned, declared);
    }

    private sealed class NoValue
    {
        public static readonly NoValue Instance = new();
    }

    /// <summary>What MVC does with each kind of return value.</summary>
    private static IActionResult ToActionResult(object? value, Type? declared) => value switch
    {
        NoValue => new EmptyResult(),
        IActionResult result => result,
        IConvertToActionResult convert => convert.Convert(),
        IResult minimal => new MinimalResult(minimal),
        _ => new ObjectResult(value) { DeclaredType = declared },
    };

    /// <summary>void actions: 200 with no body.</summary>
    private sealed class EmptyResult : ActionResult
    {
        public override Task ExecuteResultAsync(ActionContext context)
        {
            context.HttpContext.Response.StatusCode = 200;
            return Task.CompletedTask;
        }
    }

    /// <summary>Results.Ok() and friends work from actions too.</summary>
    private sealed class MinimalResult(IResult result) : ActionResult
    {
        public override Task ExecuteResultAsync(ActionContext context) => result.ExecuteAsync(context.HttpContext);
    }

    // ---- Model binding and validation ------------------------------------------------------------------

    private static readonly NullabilityInfoContext s_nullability = new();

    private static object?[] Bind(ControllerAction action, Dictionary<string, string> routeValues, HttpContext context,
        ShimServiceProvider services, ModelStateDictionary modelState)
    {
        var parameters = action.Method.GetParameters();
        var args = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            var type = p.ParameterType;
            var name = p.Name ?? $"arg{i}";

            if (p.GetCustomAttribute<FromServicesAttribute>() is not null || (action.IsApiController && !IsSimple(type) && services.IsService(type)))
                args[i] = services.GetService(type) ?? throw new InvalidOperationException($"No service for type '{type.FullName}' has been registered.");
            else if (type == typeof(CancellationToken))
                args[i] = CancellationToken.None;
            else if (type == typeof(HttpContext))
                args[i] = context;
            else if (p.GetCustomAttribute<FromBodyAttribute>() is not null || (action.IsApiController && !IsSimple(type)
                && p.GetCustomAttribute<FromQueryAttribute>() is null && p.GetCustomAttribute<FromRouteAttribute>() is null))
                args[i] = FromBody(p, context, modelState);
            else if (p.GetCustomAttribute<FromHeaderAttribute>() is { } fromHeader)
                args[i] = FromText(p, context.Request.Headers.GetValueOrDefault(fromHeader.Name ?? name), fromHeader.Name ?? name, modelState);
            else if (p.GetCustomAttribute<FromRouteAttribute>() is { } fromRoute)
                args[i] = FromText(p, routeValues.GetValueOrDefault(fromRoute.Name ?? name), fromRoute.Name ?? name, modelState);
            else if (p.GetCustomAttribute<FromQueryAttribute>() is { } fromQuery)
                args[i] = IsSimple(type)
                    ? FromText(p, context.Request.Query.GetValueOrDefault(fromQuery.Name ?? name), fromQuery.Name ?? name, modelState)
                    : FromValues(type, context.Request.Query, routeValues);
            else if (IsSimple(type))
                args[i] = FromText(p, routeValues.TryGetValue(name, out var rv) ? rv : context.Request.Query.GetValueOrDefault(name), name, modelState);
            else
                // Without [ApiController], a complex parameter is built from the route and query string, not the body.
                args[i] = FromValues(type, context.Request.Query, routeValues);
        }
        return args;
    }

    private static bool IsSimple(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t == typeof(string) || t.IsPrimitive || t.IsEnum || t == typeof(decimal) || t == typeof(Guid)
            || t == typeof(DateTime) || t == typeof(DateOnly) || t == typeof(TimeOnly) || t == typeof(DateTimeOffset) || t == typeof(TimeSpan);
    }

    /// <summary>A simple value from the route, query string or a header. Missing is fine (default) unless it's required.</summary>
    private static object? FromText(ParameterInfo p, string? text, string key, ModelStateDictionary modelState)
    {
        var type = p.ParameterType;
        if (text is null)
        {
            if (ImplicitlyRequired(p.ParameterType, s_nullability.Create(p)) && !p.HasDefaultValue)
                modelState.AddModelError(key, $"The {key} field is required.");
            return p.HasDefaultValue ? p.DefaultValue : Default(type);
        }
        if (TryConvert(text, type, out var value)) return value;
        modelState.AddModelError(key, $"The value '{text}' is not valid.");
        return p.HasDefaultValue ? p.DefaultValue : Default(type);
    }

    private static object? Default(Type type) => type.IsValueType ? Activator.CreateInstance(type) : null;

    private static bool TryConvert(string text, Type type, out object? value)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        value = null;
        if (t == typeof(string)) { value = text; return true; }
        if (text.Length == 0 && Nullable.GetUnderlyingType(type) is not null) return true;
        try
        {
            if (t.IsEnum) return Enum.TryParse(t, text, ignoreCase: true, out value);
            if (t == typeof(Guid)) { var ok = Guid.TryParse(text, out var g); value = g; return ok; }
            if (t == typeof(DateOnly)) { var ok = DateOnly.TryParse(text, CultureInfo.InvariantCulture, out var d); value = d; return ok; }
            value = Convert.ChangeType(text, t, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception e) when (e is FormatException or OverflowException or InvalidCastException)
        {
            return false;
        }
    }

    /// <summary>MVC's [FromBody]: JSON, then validation of the object it made.</summary>
    private sealed class UnsupportedMediaType : Exception;

    private static object? FromBody(ParameterInfo p, HttpContext context, ModelStateDictionary modelState)
    {
        var body = context.Request.Body;
        if (!(context.Request.ContentType ?? "").Contains("json", StringComparison.OrdinalIgnoreCase))
            throw new UnsupportedMediaType();
        if (string.IsNullOrWhiteSpace(body))
        {
            if (!(p.HasDefaultValue || OptionalReference(p)))
                modelState.AddModelError("", "A non-empty request body is required.");
            return p.HasDefaultValue ? p.DefaultValue : Default(p.ParameterType);
        }
        object? value;
        try
        {
            value = ShimJson.Deserialize(body, p.ParameterType);
        }
        catch (JsonException ex)
        {
            modelState.AddModelError(ex.Path ?? "$", ex.Message);
            return Default(p.ParameterType);
        }
        if (value is not null) Validate(value, modelState);
        return value;
    }

    private static bool OptionalReference(ParameterInfo p) =>
        Nullable.GetUnderlyingType(p.ParameterType) is not null
        || (!p.ParameterType.IsValueType && s_nullability.Create(p).ReadState != NullabilityState.NotNull);

    /// <summary>Without [ApiController]: a complex type made from route and query values, by constructor parameter or property name.</summary>
    private static object? FromValues(Type type, IReadOnlyDictionary<string, string> query, Dictionary<string, string> route)
    {
        string? Lookup(string name) => route.TryGetValue(name, out var r) ? r : query.TryGetValue(name, out var q) ? q : null;
        var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        if (ctor is null) return Activator.CreateInstance(type);
        var args = ctor.GetParameters().Select(cp =>
            Lookup(cp.Name ?? "") is { } text && TryConvert(text, cp.ParameterType, out var v) ? v
            : cp.HasDefaultValue ? cp.DefaultValue : Default(cp.ParameterType)).ToArray();
        var instance = ctor.Invoke(args);
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(pr => pr.CanWrite && pr.SetMethod!.IsPublic))
            if (Lookup(prop.Name) is { } text && TryConvert(text, prop.PropertyType, out var v)) prop.SetValue(instance, v);
        return instance;
    }

    /// <summary>
    /// MVC's validation of a bound object: DataAnnotations attributes on each property (or on the record
    /// constructor parameter it came from), plus an implicit [Required] for non-nullable reference types.
    /// Errors are keyed by property name.
    /// </summary>
    private static void Validate(object model, ModelStateDictionary modelState)
    {
        var type = model.GetType();
        var ctorParams = type.GetConstructors().SelectMany(c => c.GetParameters())
            .GroupBy(cp => cp.Name ?? "", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(pr => pr.GetMethod is { IsPublic: true } && pr.GetIndexParameters().Length == 0))
        {
            if (prop.Name == "EqualityContract") continue;
            ctorParams.TryGetValue(prop.Name, out var ctorParam);
            var attributes = prop.GetCustomAttributes<ValidationAttribute>(inherit: true)
                .Concat(ctorParam?.GetCustomAttributes<ValidationAttribute>() ?? [])
                .ToList();
            if (!attributes.OfType<RequiredAttribute>().Any()
                && ImplicitlyRequired(prop.PropertyType, ctorParam is not null ? s_nullability.Create(ctorParam) : s_nullability.Create(prop)))
                attributes.Insert(0, new RequiredAttribute());

            var value = prop.GetValue(model);
            var validationContext = new ValidationContext(model) { MemberName = prop.Name, DisplayName = prop.Name };
            foreach (var attribute in attributes)
            {
                if (attribute.GetValidationResult(value, validationContext) is { ErrorMessage: { } message })
                    modelState.AddModelError(prop.Name, message);
            }
        }
    }

    /// <summary>MVC treats non-nullable reference types (with nullable enabled) as [Required].</summary>
    private static bool ImplicitlyRequired(Type type, NullabilityInfo info) =>
        !type.IsValueType && info.ReadState == NullabilityState.NotNull;
}
