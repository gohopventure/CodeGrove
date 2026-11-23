using Microsoft.Extensions.DependencyInjection;

namespace Codegrove.WebShim;

/// <summary>
/// The app's services, using Microsoft's real container (Microsoft.Extensions.DependencyInjection).
/// </summary>
/// <remarks>
/// Earlier versions of the shim had their own small container. It was swapped for the real one when the
/// course reached Entity Framework Core: <c>AddDbContext</c> extends Microsoft's IServiceCollection, so the
/// shim's look-alike couldn't host it. The real container runs fine on WebAssembly, and its error messages
/// (missing service, scoped-from-singleton, ValidateOnBuild) are now Microsoft's own rather than copies.
/// </remarks>
internal static class ShimServices
{
    /// <summary>
    /// Builds the root provider with the checks `dotnet run` has in Development: every registration must be
    /// constructible at Build(), and no singleton may hold a scoped service.
    /// </summary>
    public static ServiceProvider Build(IServiceCollection services) =>
        services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

    /// <summary>True when the container can supply this type (how minimal APIs infer "from services").</summary>
    public static bool IsService(this IServiceProvider provider, Type type) =>
        provider.GetService<IServiceProviderIsService>()?.IsService(type) ?? false;

    /// <summary>
    /// Creates an unregistered type (a controller) with its constructor's services from this scope, as
    /// ASP.NET Core does with ActivatorUtilities, including its message when a service is missing.
    /// </summary>
    public static object CreateInstance(this IServiceProvider provider, Type type) =>
        ActivatorUtilities.CreateInstance(provider, type);
}
