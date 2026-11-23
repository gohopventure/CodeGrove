using System.Globalization;
using Codegrove.WebShim;
using Microsoft.AspNetCore.Routing;

namespace Microsoft.AspNetCore.Routing
{
    /// <summary>Something endpoints can be mapped onto: the app itself, or a group made with MapGroup.</summary>
    public interface IEndpointRouteBuilder
    {
        IServiceProvider ServiceProvider { get; }
    }

    /// <summary>The "where" part of the shim: what a route builder needs to add an endpoint.</summary>
    internal interface IEndpointSink
    {
        WebApplicationCore Core { get; }
        string Prefix { get; }
    }
}

namespace Microsoft.AspNetCore.Builder
{
    /// <summary>Returned by MapGet and friends, so settings can be chained: <c>.WithName("GetBook")</c>.</summary>
    public sealed class RouteHandlerBuilder
    {
        internal RouteHandlerBuilder(Endpoint endpoint) => Endpoint = endpoint;
        internal Endpoint Endpoint { get; }

        public RouteHandlerBuilder WithName(string endpointName)
        {
            Endpoint.Name = endpointName;
            return this;
        }

        /// <summary>Accepted for real-world code; only used by API docs, which this server doesn't make.</summary>
        public RouteHandlerBuilder WithTags(params string[] tags) => this;
        public RouteHandlerBuilder WithSummary(string summary) => this;
        public RouteHandlerBuilder WithDescription(string description) => this;
    }

    /// <summary>A set of endpoints that share a start of the path: <c>var books = app.MapGroup("/books");</c></summary>
    public sealed class RouteGroupBuilder : IEndpointRouteBuilder, IEndpointSink
    {
        private readonly WebApplicationCore _core;
        private readonly string _prefix;

        internal RouteGroupBuilder(WebApplicationCore core, string prefix) => (_core, _prefix) = (core, prefix);
        WebApplicationCore IEndpointSink.Core => _core;
        string IEndpointSink.Prefix => _prefix;
        public IServiceProvider ServiceProvider => _core.Services;

        public RouteGroupBuilder WithTags(params string[] tags) => this;
    }

    public static class EndpointRouteBuilderExtensions
    {
        public static RouteHandlerBuilder MapGet(this IEndpointRouteBuilder endpoints, string pattern, Delegate handler) => Map(endpoints, pattern, ["GET"], handler);
        public static RouteHandlerBuilder MapPost(this IEndpointRouteBuilder endpoints, string pattern, Delegate handler) => Map(endpoints, pattern, ["POST"], handler);
        public static RouteHandlerBuilder MapPut(this IEndpointRouteBuilder endpoints, string pattern, Delegate handler) => Map(endpoints, pattern, ["PUT"], handler);
        public static RouteHandlerBuilder MapPatch(this IEndpointRouteBuilder endpoints, string pattern, Delegate handler) => Map(endpoints, pattern, ["PATCH"], handler);
        public static RouteHandlerBuilder MapDelete(this IEndpointRouteBuilder endpoints, string pattern, Delegate handler) => Map(endpoints, pattern, ["DELETE"], handler);

        public static RouteHandlerBuilder MapMethods(this IEndpointRouteBuilder endpoints, string pattern, IEnumerable<string> httpMethods, Delegate handler) =>
            Map(endpoints, pattern, httpMethods.Select(m => m.ToUpperInvariant()).ToArray(), handler);

        public static RouteGroupBuilder MapGroup(this IEndpointRouteBuilder endpoints, string prefix)
        {
            var sink = Sink(endpoints);
            return new RouteGroupBuilder(sink.Core, RoutePattern.Join(sink.Prefix, prefix));
        }

        private static RouteHandlerBuilder Map(IEndpointRouteBuilder endpoints, string pattern, string[] methods, Delegate handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            var sink = Sink(endpoints);
            var endpoint = new Endpoint(methods, RoutePattern.Parse(RoutePattern.Join(sink.Prefix, pattern)), handler);
            sink.Core.Endpoints.Add(endpoint);
            return new RouteHandlerBuilder(endpoint);
        }

        private static IEndpointSink Sink(IEndpointRouteBuilder endpoints) =>
            endpoints as IEndpointSink ?? throw new NotSupportedException("Codegrove's practice server can only map endpoints on the app or a MapGroup.");
    }
}

namespace Codegrove.WebShim
{
    /// <summary>One mapped endpoint: methods + route + the learner's handler.</summary>
    /// <summary>
    /// One mapped endpoint: methods + route + either a minimal API handler or a controller action.
    /// No methods means any method (a controller action without [Http…]).
    /// </summary>
    internal sealed class Endpoint(string[] methods, RoutePattern pattern, Delegate? handler, ControllerAction? action = null)
    {
        public string[] Methods { get; } = methods;
        public RoutePattern Pattern { get; } = pattern;
        public Delegate? Handler { get; } = handler;
        public ControllerAction? Action { get; } = action;
        public string? Name { get; set; }
        public bool Allows(string method) => Methods.Length == 0 || Methods.Contains(method);
        public override string ToString() => Action is { } a
            ? $"{a.Controller.FullName}.{a.Method.Name} (LearnerApp)"
            : $"HTTP: {string.Join(", ", Methods)} {Pattern.Text}";
    }

    /// <summary>A parsed route template: "/books/{id:int}" → a literal segment and a parameter with a constraint.</summary>
    internal sealed class RoutePattern
    {
        private RoutePattern(string text, List<Segment> segments) => (Text, Segments) = (text, segments);

        public string Text { get; }
        public List<Segment> Segments { get; }
        public IEnumerable<string> ParameterNames => Segments.Where(s => s.Parameter is not null).Select(s => s.Parameter!);

        internal sealed record Segment(string? Literal, string? Parameter, string[] Constraints, bool Optional, bool CatchAll);

        public static string Join(string prefix, string pattern)
        {
            var left = prefix.TrimEnd('/');
            var right = pattern.TrimStart('/');
            var joined = left + "/" + right;
            return joined.Length > 1 ? joined.TrimEnd('/') : "/";
        }

        public static RoutePattern Parse(string text)
        {
            var segments = new List<Segment>();
            foreach (var raw in text.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (raw.StartsWith('{') && raw.EndsWith('}'))
                {
                    var inner = raw[1..^1];
                    var catchAll = inner.StartsWith('*');
                    if (catchAll) inner = inner.TrimStart('*');
                    var optional = inner.EndsWith('?');
                    if (optional) inner = inner[..^1];
                    var parts = SplitConstraints(inner);
                    var name = parts[0];
                    if (name.Length == 0) throw new RoutePatternException(text, "A route parameter must have a name, like {id}.");
                    foreach (var c in parts.Skip(1)) _ = Constraint(c, text);
                    segments.Add(new(null, name, parts.Skip(1).ToArray(), optional, catchAll));
                }
                else if (raw.Contains('{') || raw.Contains('}'))
                {
                    throw new RoutePatternException(text, "Codegrove's practice server only supports parameters that fill a whole segment, like /books/{id}.");
                }
                else
                {
                    segments.Add(new(raw, null, [], false, false));
                }
            }
            return new RoutePattern("/" + string.Join('/', text.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)), segments);
        }

        /// <summary>"id:int:min(1)" → ["id", "int", "min(1)"] (colons inside brackets stay).</summary>
        private static List<string> SplitConstraints(string inner)
        {
            var parts = new List<string>();
            var depth = 0;
            var start = 0;
            for (var i = 0; i < inner.Length; i++)
            {
                if (inner[i] == '(') depth++;
                else if (inner[i] == ')') depth--;
                else if (inner[i] == ':' && depth == 0)
                {
                    parts.Add(inner[start..i]);
                    start = i + 1;
                }
            }
            parts.Add(inner[start..]);
            return parts;
        }

        /// <summary>Matches a request path. Null when it doesn't match; otherwise the route values.</summary>
        public Dictionary<string, string>? Match(string[] pathSegments)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var i = 0;
            foreach (var s in Segments)
            {
                if (s.CatchAll)
                {
                    if (i < pathSegments.Length) values[s.Parameter!] = string.Join('/', pathSegments[i..]);
                    return values;
                }
                if (i >= pathSegments.Length)
                {
                    if (s.Optional) continue;
                    return null;
                }
                var part = pathSegments[i++];
                if (s.Literal is not null)
                {
                    if (!string.Equals(s.Literal, part, StringComparison.OrdinalIgnoreCase)) return null;
                }
                else
                {
                    if (!s.Constraints.All(c => Constraint(c, Text)(part))) return null;
                    values[s.Parameter!] = part;
                }
            }
            return i == pathSegments.Length ? values : null;
        }

        /// <summary>
        /// Lower is more specific, compared segment by segment: a literal beats a constrained parameter, which
        /// beats a plain one (ASP.NET Core's route precedence, simplified).
        /// </summary>
        public int[] Precedence => Segments.Select(s =>
            s.Literal is not null ? 1 : s.CatchAll ? 5 : s.Optional ? 4 : s.Constraints.Length > 0 ? 2 : 3).ToArray();

        private static Func<string, bool> Constraint(string constraint, string pattern)
        {
            var name = constraint;
            string[] args = [];
            var open = constraint.IndexOf('(');
            if (open > 0 && constraint.EndsWith(')'))
            {
                name = constraint[..open];
                args = constraint[(open + 1)..^1].Split(',');
            }
            var inv = CultureInfo.InvariantCulture;
            long Arg(int i) => long.Parse(args[i], inv);
            return name.ToLowerInvariant() switch
            {
                "int" => v => int.TryParse(v, NumberStyles.Integer, inv, out _),
                "long" => v => long.TryParse(v, NumberStyles.Integer, inv, out _),
                "bool" => v => bool.TryParse(v, out _),
                "guid" => v => Guid.TryParse(v, out _),
                "decimal" => v => decimal.TryParse(v, NumberStyles.Number, inv, out _),
                "double" => v => double.TryParse(v, NumberStyles.Float, inv, out _),
                "float" => v => float.TryParse(v, NumberStyles.Float, inv, out _),
                "alpha" => v => v.Length > 0 && v.All(char.IsAsciiLetter),
                "min" => v => long.TryParse(v, inv, out var n) && n >= Arg(0),
                "max" => v => long.TryParse(v, inv, out var n) && n <= Arg(0),
                "range" => v => long.TryParse(v, inv, out var n) && n >= Arg(0) && n <= Arg(1),
                "minlength" => v => v.Length >= Arg(0),
                "maxlength" => v => v.Length <= Arg(0),
                "length" => v => args.Length == 1 ? v.Length == Arg(0) : v.Length >= Arg(0) && v.Length <= Arg(1),
                _ => throw new InvalidOperationException($"The constraint reference '{name}' could not be resolved to a type. Register the constraint type with 'Microsoft.AspNetCore.Routing.RouteOptions.ConstraintMap'."),
            };
        }
    }

    internal sealed class RoutePatternException(string pattern, string message)
        : Exception($"The route template '{pattern}' isn't valid. {message}");
}
