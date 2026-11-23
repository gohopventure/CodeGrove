using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Codegrove.CSharpRunner;

/// <summary>
/// Gives every run (and every check server) a fresh Entity Framework Core.
/// </summary>
/// <remarks>
/// Learner code loads into its own <see cref="CSharpCodeRunner.RunContext"/>, so its statics reset each run,
/// but EF Core lives in the shared load context. EF caches one internal service provider per options
/// shape in <c>ServiceProviderCache.Instance</c>, and that provider owns the in-memory database root.
/// Without a reset, <c>UseInMemoryDatabase("shop")</c> on run 2 would see run 1's rows, typed by run 1's
/// entity classes, and fail with "[A]Author cannot be cast to [B]Author".
///
/// Tradeoff: this reaches a private field of an EF "internal" type by reflection. It's pinned to the EF
/// version the runner embeds, and <see cref="Reset"/> fails loudly in tests (EfTests) if EF renames it.
/// </remarks>
internal static class EfReset
{
    private const string CacheTypeName = "Microsoft.EntityFrameworkCore.Internal.ServiceProviderCache";

    private static readonly Lazy<Func<IDictionary?>> Cache = new(FindCache);

    /// <summary>Drops every cached EF internal service provider (and with it, every in-memory database).</summary>
    public static void Reset()
    {
        var cache = Cache.Value();
        if (cache is null) return;
        foreach (var value in cache.Values)
        {
            // Each value is a (IServiceProvider, IDictionary) tuple; dispose the provider so its singletons go too.
            if (value is ITuple { Length: > 0 } t && t[0] is IDisposable d)
            {
                try { d.Dispose(); } catch { /* a learner's half-built context mustn't block the next run */ }
            }
        }
        cache.Clear();
    }

    /// <summary>True when the reset can find EF's cache (false only if EF changed its internals).</summary>
    internal static bool IsSupported => Cache.Value() is not null;

    private static Func<IDictionary?> FindCache()
    {
        var type = typeof(Microsoft.EntityFrameworkCore.DbContext).Assembly.GetType(CacheTypeName);
        var instance = type?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
        var field = type?.GetField("_configurations", BindingFlags.NonPublic | BindingFlags.Instance);
        if (instance is null || field is null) return () => null;
        return () => field.GetValue(instance.GetValue(null)) as IDictionary;
    }
}
