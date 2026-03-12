using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using P2L.Mqtt;
using Spectre.Console.Cli;

namespace P2L.CLI.Infrastructure;

/// <summary>Bridges Spectre.Console.Cli with Microsoft DI.</summary>
public sealed class TypeRegistrar : ITypeRegistrar, IAsyncDisposable
{
    private readonly IServiceCollection _services;
    private ServiceProvider? _provider;

    public TypeRegistrar(IServiceCollection services) => _services = services;

    public ITypeResolver Build()
    {
        _provider = _services.BuildServiceProvider();
        // Auto-start OfflineBufferService so every command (including one-shot commands
        // like `light on` and `pick`) writes events to the buffer file without needing
        // explicit wiring in each command class. Start() is idempotent — RootCommand and
        // RunPickCommand calling it again is a safe no-op.
        _provider.GetService<OfflineBufferService>()?.Start();
        return new TypeResolver(_provider);
    }

    /// <summary>Properly disposes the ServiceProvider async so IAsyncDisposable singletons are cleaned up.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
            await _provider.DisposeAsync();
    }

    [UnconditionalSuppressMessage("AOT", "IL2067", Justification = "Spectre.Console.Cli DI bridge; types are registered explicitly by the app")]
    public void Register(Type service, Type implementation)
        => _services.AddSingleton(service, implementation);

    public void RegisterInstance(Type service, object implementation)
        => _services.AddSingleton(service, implementation);

    public void RegisterLazy(Type service, Func<object> factory)
        => _services.AddSingleton(service, _ => factory());
}

public sealed class TypeResolver : ITypeResolver, IDisposable
{
    private readonly IServiceProvider _provider;
    public TypeResolver(IServiceProvider provider) => _provider = provider;
    public object? Resolve(Type? type) => type is null ? null : _provider.GetService(type);

    /// <summary>
    /// Calls DisposeAsync on the ServiceProvider so IAsyncDisposable-only singletons
    /// (e.g. HeartbeatService) are disposed correctly — ServiceProvider.Dispose() (sync)
    /// throws for IAsyncDisposable-only services.
    /// </summary>
    public void Dispose()
    {
        if (_provider is IAsyncDisposable ad)
            ad.DisposeAsync().AsTask().GetAwaiter().GetResult();
        else
            (_provider as IDisposable)?.Dispose();
    }
}
