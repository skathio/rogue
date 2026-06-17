using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SkathIO.Rogue;

/// <summary>Extension methods for registering SkathIO.Rogue with <see cref="IServiceCollection"/>.</summary>
public static class RogueServiceCollectionExtensions
{
    /// <summary>Registers SkathIO.Rogue services with the DI container.</summary>
    public static IServiceCollection AddRogue(this IServiceCollection services, Action<RogueOptions>? configure = null)
    {
        var options = new RogueOptions();
        configure?.Invoke(options);

        // Register options so Mediator can inject it
        services.TryAddSingleton(options);

        // Flip the observability gate (FR-45). The ActivitySource/Meter are process-global (that is
        // the System.Diagnostics model), so the gate is a process-global static too. Set unconditionally
        // so a later AddRogue(...EnableTelemetry = false) can turn it back off within the same process.
        RogueTelemetry.Enabled = options.EnableTelemetry;

        // Register the mediator and entry-point interfaces
        services.TryAddTransient<IMediator, Mediator>();
        services.TryAddTransient<ISender>(sp => sp.GetRequiredService<IMediator>());
        services.TryAddTransient<IPublisher>(sp => sp.GetRequiredService<IMediator>());

        // Invoke every generator-wired registrar (PD-33/PD-38). The bridge is an append-only,
        // order-preserving registry rather than a single last-writer-wins slot: each consumer
        // compilation's module initializer (net5+) appends its registrar via RogueRegistrationBridge.Register;
        // on ns2.0 the consumer calls RogueGeneratedRegistration.Register(services, options) explicitly.
        // We snapshot the registry under the bridge's lock and invoke every distinct registrar in
        // append order, so no compilation's registrations are silently clobbered. Each registrar's
        // generated body is itself idempotent (TryAdd*/TryAddEnumerable, PD-38), so a double-AddRogue()
        // or a duplicate registrar does not double-register.
        foreach (var registrar in RogueRegistrationBridge.SnapshotRegistrars())
        {
            registrar(services, options);
        }

        // Fallback dispatcher (PD-45). A handler-less consumer's generated module initializer is
        // suppressed (it would only contribute an empty, conflicting RogueDispatcherImpl), so no
        // registrar may have registered a RogueDispatcher. Register the throwing base as a last resort
        // via TryAddScoped so IMediator still resolves — a populated registrar already registered its
        // concrete RogueDispatcherImpl, making this a no-op there. The base's Send/Publish/CreateStream
        // throw NotImplementedException("No generated dispatcher found..."), which is the correct
        // "no handler" behavior for a genuinely handler-less consumer.
        services.TryAddScoped<RogueDispatcher>();

        return services;
    }
}
