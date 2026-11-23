using System.Collections.Concurrent;
using System.Reflection;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>How long one created service lives.</summary>
public enum ServiceLifetime { Singleton, Scoped, Transient }

/// <summary>One registration: "when someone asks for ServiceType, give them …".</summary>
public sealed class ServiceDescriptor
{
    public ServiceDescriptor(Type serviceType, Type implementationType, ServiceLifetime lifetime)
        => (ServiceType, ImplementationType, Lifetime) = (serviceType, implementationType, lifetime);

    public ServiceDescriptor(Type serviceType, object instance)
        => (ServiceType, ImplementationInstance, Lifetime) = (serviceType, instance, ServiceLifetime.Singleton);

    public ServiceDescriptor(Type serviceType, Func<IServiceProvider, object> factory, ServiceLifetime lifetime)
        => (ServiceType, ImplementationFactory, Lifetime) = (serviceType, factory, lifetime);

    public Type ServiceType { get; }
    public Type? ImplementationType { get; }
    public object? ImplementationInstance { get; }
    public Func<IServiceProvider, object>? ImplementationFactory { get; }
    public ServiceLifetime Lifetime { get; }

    /// <summary>Same text as Microsoft.Extensions.DependencyInjection, which appears in its error messages.</summary>
    public override string ToString()
    {
        var what = ImplementationType is not null ? $"ImplementationType: {ImplementationType.FullName}"
            : ImplementationFactory is not null ? $"ImplementationFactory: {ImplementationFactory.Method}"
            : $"ImplementationInstance: {ImplementationInstance}";
        return $"ServiceType: {ServiceType.FullName} Lifetime: {Lifetime} {what}";
    }
}

public interface IServiceCollection : IList<ServiceDescriptor>;

public sealed class ServiceCollection : List<ServiceDescriptor>, IServiceCollection;

public interface IServiceScope : IDisposable
{
    IServiceProvider ServiceProvider { get; }
}

public interface IServiceScopeFactory
{
    IServiceScope CreateScope();
}

public static class ServiceCollectionServiceExtensions
{
    public static IServiceCollection AddSingleton<TService, TImplementation>(this IServiceCollection services)
        where TService : class where TImplementation : class, TService
        => services.With(new(typeof(TService), typeof(TImplementation), ServiceLifetime.Singleton));

    public static IServiceCollection AddSingleton<TService>(this IServiceCollection services) where TService : class
        => services.With(new(typeof(TService), typeof(TService), ServiceLifetime.Singleton));

    public static IServiceCollection AddSingleton<TService>(this IServiceCollection services, TService implementationInstance) where TService : class
        => services.With(new(typeof(TService), (object)implementationInstance));

    public static IServiceCollection AddSingleton<TService>(this IServiceCollection services, Func<IServiceProvider, TService> implementationFactory) where TService : class
        => services.With(new(typeof(TService), implementationFactory, ServiceLifetime.Singleton));

    public static IServiceCollection AddScoped<TService, TImplementation>(this IServiceCollection services)
        where TService : class where TImplementation : class, TService
        => services.With(new(typeof(TService), typeof(TImplementation), ServiceLifetime.Scoped));

    public static IServiceCollection AddScoped<TService>(this IServiceCollection services) where TService : class
        => services.With(new(typeof(TService), typeof(TService), ServiceLifetime.Scoped));

    public static IServiceCollection AddScoped<TService>(this IServiceCollection services, Func<IServiceProvider, TService> implementationFactory) where TService : class
        => services.With(new(typeof(TService), implementationFactory, ServiceLifetime.Scoped));

    public static IServiceCollection AddTransient<TService, TImplementation>(this IServiceCollection services)
        where TService : class where TImplementation : class, TService
        => services.With(new(typeof(TService), typeof(TImplementation), ServiceLifetime.Transient));

    public static IServiceCollection AddTransient<TService>(this IServiceCollection services) where TService : class
        => services.With(new(typeof(TService), typeof(TService), ServiceLifetime.Transient));

    public static IServiceCollection AddTransient<TService>(this IServiceCollection services, Func<IServiceProvider, TService> implementationFactory) where TService : class
        => services.With(new(typeof(TService), implementationFactory, ServiceLifetime.Transient));

    private static IServiceCollection With(this IServiceCollection services, ServiceDescriptor descriptor)
    {
        services.Add(descriptor);
        return services;
    }
}

public static class ServiceProviderServiceExtensions
{
    public static T? GetService<T>(this IServiceProvider provider) => (T?)provider.GetService(typeof(T));

    public static object GetRequiredService(this IServiceProvider provider, Type serviceType) =>
        provider.GetService(serviceType) ?? throw new InvalidOperationException($"No service for type '{serviceType.FullName}' has been registered.");

    public static T GetRequiredService<T>(this IServiceProvider provider) where T : notnull => (T)provider.GetRequiredService(typeof(T));

    public static IServiceScope CreateScope(this IServiceProvider provider) =>
        provider.GetRequiredService<IServiceScopeFactory>().CreateScope();
}

/// <summary>
/// The container. Like Microsoft's in Development: scopes are validated (a singleton can't hold a scoped
/// service) and every registration is checked when the app is built, with the same error messages.
/// </summary>
internal sealed class ShimServiceProvider : IServiceProvider, IServiceScopeFactory, IDisposable
{
    private readonly Dictionary<Type, ServiceDescriptor> _last;
    private readonly ShimServiceProvider? _root;
    private readonly ConcurrentDictionary<ServiceDescriptor, object> _created = new();

    public ShimServiceProvider(IEnumerable<ServiceDescriptor> services)
    {
        // The last registration for a type wins, as in Microsoft's container.
        _last = [];
        foreach (var d in services) _last[d.ServiceType] = d;
    }

    private ShimServiceProvider(ShimServiceProvider root) => (_last, _root) = (root._last, root);

    private bool IsRoot => _root is null;
    private ShimServiceProvider Root => _root ?? this;

    public bool IsService(Type type) =>
        type == typeof(IServiceProvider) || type == typeof(IServiceScopeFactory) || Find(type) is not null;

    public object? GetService(Type serviceType) => Resolve(serviceType, owner: null);

    public IServiceScope CreateScope() => new Scope(new ShimServiceProvider(Root));

    /// <summary>ValidateOnBuild: every registration must be constructible, without creating anything.</summary>
    public void Validate()
    {
        var problems = new List<string>();
        foreach (var d in _last.Values)
        {
            try
            {
                Check(d, d.Lifetime == ServiceLifetime.Singleton ? d : null, []);
            }
            catch (InvalidOperationException ex)
            {
                problems.Add($"Error while validating the service descriptor '{d}': {ex.Message}");
            }
        }
        if (problems.Count > 0)
            throw new AggregateException("Some services are not able to be constructed (" + string.Join(") (", problems) + ")");
    }

    private void Check(ServiceDescriptor d, ServiceDescriptor? singletonOwner, HashSet<Type> path)
    {
        if (d.ImplementationType is not { } impl) return;
        if (!path.Add(d.ServiceType))
            throw new InvalidOperationException($"A circular dependency was detected for the service of type '{d.ServiceType.FullName}'.");
        foreach (var p in PickConstructor(impl).GetParameters())
        {
            if (p.ParameterType == typeof(IServiceProvider) || p.ParameterType == typeof(IServiceScopeFactory)) continue;
            var dep = Find(p.ParameterType);
            if (dep is null)
            {
                if (p.HasDefaultValue) continue;
                throw new InvalidOperationException($"Unable to resolve service for type '{p.ParameterType.FullName}' while attempting to activate '{impl.FullName}'.");
            }
            if (singletonOwner is not null && dep.Lifetime == ServiceLifetime.Scoped)
                throw new InvalidOperationException($"Cannot consume scoped service '{dep.ServiceType.FullName}' from singleton '{singletonOwner.ServiceType.FullName}'.");
            Check(dep, singletonOwner, path);
        }
        path.Remove(d.ServiceType);
    }

    private ServiceDescriptor? Find(Type type) => _last.GetValueOrDefault(type);

    private object? Resolve(Type type, ServiceDescriptor? owner)
    {
        if (type == typeof(IServiceProvider)) return this;
        if (type == typeof(IServiceScopeFactory)) return Root;
        if (Find(type) is not { } d) return null;

        switch (d.Lifetime)
        {
            case ServiceLifetime.Singleton:
                return Root._created.GetOrAdd(d, _ => Root.Create(d));
            case ServiceLifetime.Scoped:
                if (IsRoot)
                    throw new InvalidOperationException(owner is null
                        ? $"Cannot resolve scoped service '{d.ServiceType.FullName}' from root provider."
                        : $"Cannot consume scoped service '{d.ServiceType.FullName}' from singleton '{owner.ServiceType.FullName}'.");
                return _created.GetOrAdd(d, _ => Create(d));
            default:
                return Create(d);
        }
    }

    private object Create(ServiceDescriptor d)
    {
        if (d.ImplementationInstance is { } instance) return instance;
        if (d.ImplementationFactory is { } factory) return factory(this);
        var impl = d.ImplementationType!;
        var ctor = PickConstructor(impl);
        var args = ctor.GetParameters().Select(p =>
            Resolve(p.ParameterType, d.Lifetime == ServiceLifetime.Singleton ? d : null)
            ?? (p.HasDefaultValue ? p.DefaultValue
                : throw new InvalidOperationException($"Unable to resolve service for type '{p.ParameterType.FullName}' while attempting to activate '{impl.FullName}'."))).ToArray();
        try
        {
            return ctor.Invoke(args);
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            // Show the learner's own exception, not reflection's wrapper.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw;
        }
    }

    /// <summary>The public constructor with the most parameters this container can supply (Microsoft's rule, simplified).</summary>
    private ConstructorInfo PickConstructor(Type impl)
    {
        var ctors = impl.GetConstructors();
        if (ctors.Length == 0)
            throw new InvalidOperationException($"A suitable constructor for type '{impl.FullName}' could not be located. Ensure the type is concrete and all parameters of a public constructor are either registered as services or passed as arguments. Also ensure no extraneous arguments are provided.");
        return ctors
            .OrderByDescending(c => c.GetParameters().Count(p => IsService(p.ParameterType)))
            .ThenByDescending(c => c.GetParameters().Length)
            .First();
    }

    public void Dispose()
    {
        foreach (var o in _created.Values) (o as IDisposable)?.Dispose();
    }

    private sealed class Scope(ShimServiceProvider provider) : IServiceScope
    {
        public IServiceProvider ServiceProvider => provider;
        public void Dispose() => provider.Dispose();
    }
}
