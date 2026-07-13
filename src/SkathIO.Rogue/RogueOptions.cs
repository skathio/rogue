using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;

namespace SkathIO.Rogue;

/// <summary>Configuration options for SkathIO.Rogue.</summary>
public sealed class RogueOptions
{
    /// <summary>
    /// Default lifetime for generated <b>handler</b> (and stream handler) self-registrations.
    /// Default: <see cref="ServiceLifetime.Transient"/>.
    /// <para>
    /// This setting governs handlers only. Pipeline behaviors — and the
    /// <c>IReadOnlyList&lt;IPipelineBehavior&lt;,&gt;&gt;</c> factory that resolves them — are
    /// always registered <see cref="ServiceLifetime.Transient"/>, regardless of this value. A
    /// behavior pinned to this option's lifetime would let <c>Lifetime = Singleton</c> turn a
    /// behavior that depends on a Scoped service (e.g. a FluentValidation <c>IValidator&lt;T&gt;</c>)
    /// into a captive dependency, so behavior lifetime is deliberately decoupled from this option.
    /// </para>
    /// </summary>
    public ServiceLifetime Lifetime { get; set; } = ServiceLifetime.Transient;

    /// <summary>Event publish strategy. Default: <see cref="ForeachAwaitPublisher"/>.</summary>
    public IEventPublisher EventPublisher { get; set; } = new ForeachAwaitPublisher();

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
