using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace Codegrove.WebApi.ParityTests;

/// <summary>
/// Makes Codegrove.WebShim loadable at run time, for the runner's side of each comparison. It isn't
/// referenced at compile time (its types share ASP.NET Core's names), so it's copied into shim/ and
/// resolved by name.
/// </summary>
internal static class ShimLoader
{
    [ModuleInitializer]
    internal static void Init() =>
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            if (name.Name != "Codegrove.WebShim") return null;
            var path = Path.Combine(AppContext.BaseDirectory, "shim", "Codegrove.WebShim.dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
}
