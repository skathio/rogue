using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;

namespace SkathIO.Rogue;

/// <summary>Configuration options for SkathIO.Rogue.</summary>
public sealed class RogueOptions
{
    /// <summary>Default handler/behavior lifetime. Default: <see cref="ServiceLifetime.Transient"/>.</summary>
    public ServiceLifetime Lifetime { get; set; } = ServiceLifetime.Transient;

    /// <summary>Notification publish strategy. Default: <see cref="ForeachAwaitPublisher"/>.</summary>
    public INotificationPublisher NotificationPublisher { get; set; } = new ForeachAwaitPublisher();

    /// <summary>Whether to enable the <c>object</c>-typed dispatch path. Default: false.</summary>
    public bool EnableObjectDispatch { get; set; }

    /// <summary>Whether to emit OTel ActivitySource/Meter instrumentation. Default: false.</summary>
    public bool EnableTelemetry { get; set; }

    /// <summary>
    /// Runtime behavior registrations contributed via <see cref="AddOpenBehavior"/> /
    /// <see cref="AddBehavior{TBehavior}"/>. Read by the generated <c>Register</c> method (in the
    /// consumer's compilation) to apply the PD-13a ordering sort key, so it must be <c>public</c> —
    /// an <c>internal</c> property produces CS0122 in every real consumer (PD-19).
    /// </summary>
    public List<BehaviorRegistration> BehaviorRegistrations { get; } = new List<BehaviorRegistration>();

    /// <summary>Registers an open-generic behavior applied to all requests.</summary>
    public RogueOptions AddOpenBehavior(Type behaviorType, int order = 0)
    {
        BehaviorRegistrations.Add(new BehaviorRegistration(behaviorType, order, IsOpen: true));
        return this;
    }

    /// <summary>Registers a closed behavior for a specific request/response pair.</summary>
    public RogueOptions AddBehavior<TBehavior>(int order = 0) where TBehavior : class
    {
        BehaviorRegistrations.Add(new BehaviorRegistration(typeof(TBehavior), order, IsOpen: false));
        return this;
    }
}

/// <summary>
/// A single runtime behavior registration. Public because the generated <c>Register</c> method
/// reads <see cref="RogueOptions.BehaviorRegistrations"/> from the consumer's compilation (PD-19).
/// </summary>
public sealed record BehaviorRegistration(Type BehaviorType, int Order, bool IsOpen);
