using System.Text.Json;
using Codegrove.WebShim;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Microsoft.AspNetCore.Mvc
{
    /// <summary>
    /// Marks an API controller. It turns on attribute routing checks, reading complex parameters from the
    /// body, automatic 400 responses when model validation fails, and problem details for NotFound() and
    /// other client errors.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Assembly, Inherited = true)]
    public sealed class ApiControllerAttribute : Attribute;

    /// <summary>A route template for a controller or action: [Route("api/[controller]")].</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
    public sealed class RouteAttribute(string template) : Attribute
    {
        public string Template { get; } = template;
        public string? Name { get; set; }
    }

    /// <summary>The base of [HttpGet], [HttpPost] and friends: the methods an action answers, and an optional template.</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
    public abstract class HttpMethodAttribute(IEnumerable<string> httpMethods, string? template = null) : Attribute
    {
        public IEnumerable<string> HttpMethods { get; } = httpMethods.ToArray();
        public string? Template { get; } = template;
        public string? Name { get; set; }
    }

    public sealed class HttpGetAttribute : HttpMethodAttribute
    {
        public HttpGetAttribute() : base(["GET"]) { }
        public HttpGetAttribute(string template) : base(["GET"], template) { }
    }

    public sealed class HttpPostAttribute : HttpMethodAttribute
    {
        public HttpPostAttribute() : base(["POST"]) { }
        public HttpPostAttribute(string template) : base(["POST"], template) { }
    }

    public sealed class HttpPutAttribute : HttpMethodAttribute
    {
        public HttpPutAttribute() : base(["PUT"]) { }
        public HttpPutAttribute(string template) : base(["PUT"], template) { }
    }

    public sealed class HttpPatchAttribute : HttpMethodAttribute
    {
        public HttpPatchAttribute() : base(["PATCH"]) { }
        public HttpPatchAttribute(string template) : base(["PATCH"], template) { }
    }

    public sealed class HttpDeleteAttribute : HttpMethodAttribute
    {
        public HttpDeleteAttribute() : base(["DELETE"]) { }
        public HttpDeleteAttribute(string template) : base(["DELETE"], template) { }
    }

    /// <summary>A public method that isn't an action.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class NonActionAttribute : Attribute;

    /// <summary>Documents a response for API tools. Accepted for real-world code; it doesn't change anything.</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
    public sealed class ProducesResponseTypeAttribute : Attribute
    {
        public ProducesResponseTypeAttribute(int statusCode) => StatusCode = statusCode;
        public ProducesResponseTypeAttribute(Type type, int statusCode) => (Type, StatusCode) = (type, statusCode);
        public Type? Type { get; }
        public int StatusCode { get; }
    }

    /// <summary>What an action is running in: the request and its HttpContext.</summary>
    public class ActionContext
    {
        public ActionContext() { }
        internal ActionContext(HttpContext httpContext, ControllerRun run) => (HttpContext, Run) = (httpContext, run);
        public HttpContext HttpContext { get; set; } = null!;
        internal ControllerRun? Run { get; }
    }

    /// <summary>Anything an action can return that knows how to write the response.</summary>
    public interface IActionResult
    {
        Task ExecuteResultAsync(ActionContext context);
    }

    public abstract class ActionResult : IActionResult
    {
        public virtual Task ExecuteResultAsync(ActionContext context) => Task.CompletedTask;
    }

    /// <summary>Implemented by ActionResult&lt;T&gt;, so the framework can unwrap it.</summary>
    public interface IConvertToActionResult
    {
        IActionResult Convert();
    }

    /// <summary>Either a value (200 with it as JSON) or any ActionResult, like NotFound().</summary>
    public sealed class ActionResult<TValue> : IConvertToActionResult
    {
        public ActionResult(TValue value) => Value = value;
        public ActionResult(ActionResult result) => Result = result;

        public ActionResult? Result { get; }
        public TValue? Value { get; }

        public static implicit operator ActionResult<TValue>(TValue value) => new(value);
        public static implicit operator ActionResult<TValue>(ActionResult result) => new(result);

        public IActionResult Convert() => Result ?? (IActionResult)new ObjectResult(Value) { DeclaredType = typeof(TValue) };
    }

    /// <summary>A status code with no body. Inside an [ApiController], 4xx ones send problem details.</summary>
    public class StatusCodeResult(int statusCode) : ActionResult
    {
        public int StatusCode { get; } = statusCode;
        public override Task ExecuteResultAsync(ActionContext context) => MvcWrite.Status(context, StatusCode);
    }

    public sealed class OkResult() : StatusCodeResult(200);
    public sealed class NoContentResult() : StatusCodeResult(204);
    public sealed class NotFoundResult() : StatusCodeResult(404);
    public sealed class BadRequestResult() : StatusCodeResult(400);
    public sealed class ConflictResult() : StatusCodeResult(409);
    public sealed class UnauthorizedResult() : StatusCodeResult(401);
    public sealed class UnprocessableEntityResult() : StatusCodeResult(422);

    /// <summary>A value written with a status code: JSON for objects, text for strings, 204 for null.</summary>
    public class ObjectResult(object? value) : ActionResult
    {
        public object? Value { get; set; } = value;
        public int? StatusCode { get; set; }
        public Type? DeclaredType { get; set; }
        public override Task ExecuteResultAsync(ActionContext context) => MvcWrite.Object(context, this);
    }

    public sealed class OkObjectResult : ObjectResult
    {
        public OkObjectResult(object? value) : base(value) => StatusCode = 200;
    }

    public sealed class NotFoundObjectResult : ObjectResult
    {
        public NotFoundObjectResult(object? value) : base(value) => StatusCode = 404;
    }

    public sealed class BadRequestObjectResult : ObjectResult
    {
        public BadRequestObjectResult(object? error) : base(error) => StatusCode = 400;
    }

    public sealed class ConflictObjectResult : ObjectResult
    {
        public ConflictObjectResult(object? error) : base(error) => StatusCode = 409;
    }

    public sealed class UnprocessableEntityObjectResult : ObjectResult
    {
        public UnprocessableEntityObjectResult(object? error) : base(error) => StatusCode = 422;
    }

    public sealed class CreatedResult : ObjectResult
    {
        public CreatedResult(string? location, object? value) : base(value) => (Location, StatusCode) = (location, 201);
        public string? Location { get; set; }
    }

    public sealed class CreatedAtActionResult(string? actionName, string? controllerName, object? routeValues, object? value) : ObjectResult(value)
    {
        public string? ActionName { get; set; } = actionName;
        public string? ControllerName { get; set; } = controllerName;
        public object? RouteValues { get; set; } = routeValues;
    }

    public sealed class ContentResult : ActionResult
    {
        public string? Content { get; set; }
        public string? ContentType { get; set; }
        public int? StatusCode { get; set; }
        public override Task ExecuteResultAsync(ActionContext context)
        {
            var response = context.HttpContext.Response;
            response.StatusCode = StatusCode ?? 200;
            response.ContentType = ContentType ?? "text/plain; charset=utf-8";
            return response.WriteAsync(Content ?? "");
        }
    }

    /// <summary>ProblemDetails plus the field errors MVC found (ModelState).</summary>
    public class ValidationProblemDetails : HttpValidationProblemDetails
    {
        public ValidationProblemDetails() => Title = "One or more validation errors occurred.";
        public ValidationProblemDetails(ModelStateDictionary modelState) : base(modelState.ToErrors()) => Title = "One or more validation errors occurred.";
        public ValidationProblemDetails(IDictionary<string, string[]> errors) : base(errors) => Title = "One or more validation errors occurred.";
    }

    /// <summary>The base class for API controllers: helpers that make results, and the request.</summary>
    public abstract class ControllerBase
    {
        private ModelStateDictionary? _modelState;

        public HttpContext HttpContext { get; internal set; } = null!;
        public HttpRequest Request => HttpContext.Request;
        public HttpResponse Response => HttpContext.Response;
        public ModelStateDictionary ModelState => _modelState ??= new ModelStateDictionary();

        internal void UseModelState(ModelStateDictionary modelState) => _modelState = modelState;

        [NonAction] public virtual OkResult Ok() => new();
        [NonAction] public virtual OkObjectResult Ok(object? value) => new(value);
        [NonAction] public virtual NoContentResult NoContent() => new();
        [NonAction] public virtual NotFoundResult NotFound() => new();
        [NonAction] public virtual NotFoundObjectResult NotFound(object? value) => new(value);
        [NonAction] public virtual BadRequestResult BadRequest() => new();
        [NonAction] public virtual BadRequestObjectResult BadRequest(object? error) => new(error);
        [NonAction] public virtual BadRequestObjectResult BadRequest(ModelStateDictionary modelState) => new(new SerializableError(modelState));
        [NonAction] public virtual ConflictResult Conflict() => new();
        [NonAction] public virtual ConflictObjectResult Conflict(object? error) => new(error);
        [NonAction] public virtual UnprocessableEntityResult UnprocessableEntity() => new();
        [NonAction] public virtual UnprocessableEntityObjectResult UnprocessableEntity(object? error) => new(error);
        [NonAction] public virtual UnauthorizedResult Unauthorized() => new();
        [NonAction] public virtual StatusCodeResult StatusCode(int statusCode) => new(statusCode);
        [NonAction] public virtual ObjectResult StatusCode(int statusCode, object? value) => new(value) { StatusCode = statusCode };
        [NonAction] public virtual ContentResult Content(string content) => new() { Content = content };
        [NonAction] public virtual ContentResult Content(string content, string contentType) => new() { Content = content, ContentType = contentType };
        [NonAction] public virtual CreatedResult Created(string? uri, object? value) => new(uri, value) { StatusCode = 201 };

        [NonAction]
        public virtual CreatedAtActionResult CreatedAtAction(string? actionName, object? value) => CreatedAtAction(actionName, null, null, value);

        [NonAction]
        public virtual CreatedAtActionResult CreatedAtAction(string? actionName, object? routeValues, object? value) => CreatedAtAction(actionName, null, routeValues, value);

        [NonAction]
        public virtual CreatedAtActionResult CreatedAtAction(string? actionName, string? controllerName, object? routeValues, object? value) =>
            new(actionName, controllerName, routeValues, value) { StatusCode = 201 };

        [NonAction]
        public virtual ObjectResult Problem(string? detail = null, string? instance = null, int? statusCode = null, string? title = null, string? type = null)
        {
            var problem = new ProblemDetails { Detail = detail, Instance = instance, Status = statusCode ?? 500, Title = title, Type = type };
            var defaults = ShimJson.ProblemDefaults(problem.Status.Value);
            problem.Title ??= defaults.Title;
            problem.Type ??= defaults.Type;
            return new ObjectResult(problem) { StatusCode = problem.Status };
        }

        [NonAction]
        public virtual ActionResult ValidationProblem() => ValidationProblem(ModelState);

        [NonAction]
        public virtual ActionResult ValidationProblem(ModelStateDictionary modelStateDictionary)
        {
            var problem = new ValidationProblemDetails(modelStateDictionary)
            {
                Status = 400,
                Type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
            };
            return new ObjectResult(problem) { StatusCode = 400 };
        }
    }

    /// <summary>ModelState as a plain dictionary of field errors, as BadRequest(ModelState) writes it.</summary>
    public sealed class SerializableError : Dictionary<string, object>
    {
        public SerializableError() { }
        public SerializableError(ModelStateDictionary modelState)
        {
            foreach (var (key, messages) in modelState.ToErrors()) this[key] = messages;
        }
    }
}

namespace Microsoft.AspNetCore.Mvc.ModelBinding
{
    /// <summary>What model binding and validation found wrong, by field. IsValid when nothing.</summary>
    public sealed class ModelStateDictionary
    {
        private readonly Dictionary<string, List<string>> _errors = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _order = [];

        public bool IsValid => ErrorCount == 0;
        public int ErrorCount => _errors.Values.Sum(e => e.Count);
        public IEnumerable<string> Keys => _order;

        public void AddModelError(string key, string errorMessage)
        {
            if (!_errors.TryGetValue(key, out var list))
            {
                list = [];
                _errors[key] = list;
                _order.Add(key);
            }
            list.Add(errorMessage);
        }

        public void Clear()
        {
            _errors.Clear();
            _order.Clear();
        }

        internal IDictionary<string, string[]> ToErrors()
        {
            var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var key in _order) result[key] = [.. _errors[key]];
            return result;
        }
    }
}

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Returned by AddControllers, for chaining MVC settings (none are needed here).</summary>
    public interface IMvcBuilder
    {
        IServiceCollection Services { get; }
    }

    public static class MvcServiceCollectionExtensions
    {
        /// <summary>Adds what controllers need. Map them with app.MapControllers().</summary>
        public static IMvcBuilder AddControllers(this IServiceCollection services)
        {
            if (!services.Any(d => d.ServiceType == typeof(ControllersAdded)))
                services.Add(new ServiceDescriptor(typeof(ControllersAdded), new ControllersAdded()));
            return new MvcBuilder(services);
        }

        private sealed class MvcBuilder(IServiceCollection services) : IMvcBuilder
        {
            public IServiceCollection Services { get; } = services;
        }
    }
}

namespace Microsoft.AspNetCore.Builder
{
    /// <summary>Returned by MapControllers. Settings can be chained on it (none are needed here).</summary>
    public sealed class ControllerActionEndpointConventionBuilder;

    public static class ControllerEndpointRouteBuilderExtensions
    {
        /// <summary>Maps every attribute-routed action of every public controller in your code.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static ControllerActionEndpointConventionBuilder MapControllers(this Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints)
        {
            var learnerCode = System.Reflection.Assembly.GetCallingAssembly();
            Controllers.Map(endpoints, learnerCode);
            return new ControllerActionEndpointConventionBuilder();
        }
    }
}

namespace Codegrove.WebShim
{
    /// <summary>Registered by AddControllers, so MapControllers can tell it was called.</summary>
    internal sealed class ControllersAdded;

    /// <summary>Writing MVC results, as MVC's output formatters and client-error mapping do.</summary>
    internal static class MvcWrite
    {
        public static Task Status(Microsoft.AspNetCore.Mvc.ActionContext context, int status)
        {
            var response = context.HttpContext.Response;
            response.StatusCode = status;
            // [ApiController] turns client-error status results (NotFound(), BadRequest()…) into problem details.
            if (status >= 400 && context.Run?.IsApiController == true)
            {
                var defaults = ShimJson.ProblemDefaults(status);
                var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails { Type = defaults.Type, Title = defaults.Title, Status = status };
                response.ContentType = "application/problem+json; charset=utf-8";
                return response.WriteAsync(ShimJson.Serialize(problem));
            }
            return Task.CompletedTask;
        }

        public static Task Object(Microsoft.AspNetCore.Mvc.ActionContext context, Microsoft.AspNetCore.Mvc.ObjectResult result)
        {
            var response = context.HttpContext.Response;
            var status = result.StatusCode ?? 200;
            if (result is Microsoft.AspNetCore.Mvc.CreatedResult created && created.Location is not null)
                response.Headers["Location"] = created.Location;
            if (result is Microsoft.AspNetCore.Mvc.CreatedAtActionResult atAction)
                response.Headers["Location"] = context.Run!.LinkTo(atAction.ActionName, atAction.ControllerName, atAction.RouteValues);

            switch (result.Value)
            {
                case null:
                    // MVC's no-content formatter: a null value with 200 becomes 204 with no body.
                    response.StatusCode = status == 200 ? 204 : status;
                    return Task.CompletedTask;
                case string text:
                    response.StatusCode = status;
                    response.ContentType = "text/plain; charset=utf-8";
                    return response.WriteAsync(text);
                case Microsoft.AspNetCore.Mvc.ProblemDetails problem:
                    response.StatusCode = status;
                    response.ContentType = "application/problem+json; charset=utf-8";
                    return response.WriteAsync(ShimJson.Serialize(problem));
                default:
                    response.StatusCode = status;
                    response.ContentType = "application/json; charset=utf-8";
                    return response.WriteAsync(ShimJson.Serialize(result.Value));
            }
        }
    }
}
