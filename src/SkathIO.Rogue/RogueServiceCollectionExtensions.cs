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

        // Invoke the generator-wired registration if available (PD-15).
        // On net5+, the module initializer in the consumer's generated code sets this before AddRogue is called.
        // On ns2.0, call RogueGeneratedRegistration.Register(services, options) explicitly after AddRogue().
        RogueRegistrationBridge.GeneratedRegistrar?.Invoke(services, options);

        return services;
    }
}
