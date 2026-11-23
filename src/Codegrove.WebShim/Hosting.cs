using Codegrove.WebShim;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.AspNetCore.Builder
{
    /// <summary>Collects services, then <see cref="Build"/> makes the app.</summary>
    public sealed class WebApplicationBuilder
    {
        internal WebApplicationBuilder() { }

        /// <summary>Register services here, before Build(): <c>builder.Services.AddSingleton&lt;PetBook&gt;();</c></summary>
        public IServiceCollection Services { get; } = new ServiceCollection();

        private bool _built;

        public WebApplication Build()
        {
            if (_built) throw new InvalidOperationException("Build can only be called once.");
            _built = true;
            // Development checks, as `dotnet run` has them: every service must be constructible, and no
            // singleton may hold a scoped service. The same errors as ASP.NET Core, at the same moment.
            var provider = ShimServices.Build(Services);
            var app = new WebApplication(new WebApplicationCore(provider));
            ShimHost.Built(app);
            return app;
        }
    }

    /// <summary>
    /// The web app: map endpoints on it, then call Run().
    /// Codegrove difference: Run() doesn't block or open a port. It hands the app to Codegrove, which sends
    /// it the requests from the Try it panel and from the checks.
    /// </summary>
    public sealed class WebApplication : IEndpointRouteBuilder, IEndpointSink, IDisposable, IAsyncDisposable
    {
        private readonly WebApplicationCore _core;

        internal WebApplication(WebApplicationCore core) => _core = core;

        public static WebApplicationBuilder CreateBuilder() => new();
        public static WebApplicationBuilder CreateBuilder(string[] args) => new();
        public static WebApplication Create(string[]? args = null) => new WebApplicationBuilder().Build();

        /// <summary>The app's services (for creating a scope at start-up, say).</summary>
        public IServiceProvider Services => _core.Services;
        IServiceProvider IEndpointRouteBuilder.ServiceProvider => _core.Services;
        WebApplicationCore IEndpointSink.Core => _core;
        string IEndpointSink.Prefix => "/";

        internal WebApplicationCore Core => _core;

        /// <summary>Starts the server. (In Codegrove: hands it to the Try it panel and the checks, and returns.)</summary>
        public void Run(string? url = null)
        {
            if (_core.Started) throw new InvalidOperationException("The server has already started.");
            _core.Started = true;
            _core.Url = url ?? "http://localhost:5000";
            ShimHost.Started(this);
            // A real Run() blocks here until the server shuts down, so nothing after it runs. Same here:
            // the runner catches this and treats it as the program's normal end.
            throw new ServerStartedSignal();
        }

        public Task RunAsync(string? url = null)
        {
            Run(url);
            return Task.CompletedTask;
        }

        public void Dispose() => _core.Services.Dispose();

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

namespace Codegrove.WebShim
{
    /// <summary>Ends the program at app.Run(), as a real server would (it never returns while running).</summary>
    internal sealed class ServerStartedSignal() : Exception("The server started. (Codegrove ends the program here, as app.Run() would keep it waiting for requests.)");

    /// <summary>Everything the app holds: its services and endpoints.</summary>
    internal sealed class WebApplicationCore(ServiceProvider services)
    {
        public ServiceProvider Services { get; } = services;
        public List<Endpoint> Endpoints { get; } = [];
        public bool Started { get; set; }
        public string Url { get; set; } = "http://localhost:5000";
    }
}
