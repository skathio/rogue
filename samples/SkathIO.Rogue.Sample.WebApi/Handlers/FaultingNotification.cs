using System;
using System.Threading;
using System.Threading.Tasks;
using SkathIO.Rogue;

namespace SkathIO.Rogue.Sample.WebApi;

/// <summary>
/// A notification whose two handlers both throw. Used to observe per-strategy error aggregation
/// (FR-29): the sequential <c>ForeachAwait</c> publisher surfaces the first throw; the parallel
/// <c>WhenAll</c> publisher surfaces an <see cref="AggregateException"/> with both.
/// </summary>
public sealed record FaultingNotification(int Id) : IEvent;

/// <summary>First faulting handler for <see cref="FaultingNotification"/> (FR-29).</summary>
public sealed class FaultingHandler1 : IEventHandler<FaultingNotification>
{
    /// <summary>The message thrown by this handler.</summary>
    public const string Message = "FaultingHandler1 failed";

    /// <inheritdoc />
    public ValueTask Handle(FaultingNotification notification, CancellationToken cancellationToken)
        => throw new InvalidOperationException(Message);
}

/// <summary>Second faulting handler for <see cref="FaultingNotification"/> (FR-29).</summary>
public sealed class FaultingHandler2 : IEventHandler<FaultingNotification>
{
    /// <summary>The message thrown by this handler.</summary>
    public const string Message = "FaultingHandler2 failed";

    /// <inheritdoc />
    public ValueTask Handle(FaultingNotification notification, CancellationToken cancellationToken)
        => throw new InvalidOperationException(Message);
}
