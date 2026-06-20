using System;
#if !NETSTANDARD2_0
using System.Collections.Generic;
#endif
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue;

/// <summary>
/// Default mediator implementation. Delegates to the source-generated dispatcher.
/// Prefer injecting <see cref="ISender"/> or <see cref="IPublisher"/> over this type directly.
/// </summary>
/// <remarks>
/// D2 (rogue-perf): the <see cref="RogueDispatcher"/> is constructor-injected and cached, rather than
/// resolved via <c>IServiceProvider.GetRequiredService&lt;RogueDispatcher&gt;()</c> on every dispatch.
/// This removes one DI lookup from the warm Send/Publish path. Because the dispatcher is registered
/// <c>Scoped</c> (it binds to the resolving scope so scoped handler dependencies resolve correctly),
/// the mediator and its entry-point interfaces (<see cref="IMediator"/>/<see cref="ISender"/>/
/// <see cref="IPublisher"/>) are registered <c>Scoped</c> too — a transient mediator capturing a scoped
/// dispatcher would be a captive-dependency lifetime mismatch.
/// </remarks>
public sealed class Mediator : IMediator
{
    private readonly RogueDispatcher _dispatcher;
    private readonly RogueOptions _options;

    /// <summary>Initializes the mediator with the generated dispatcher it delegates to.</summary>
    public Mediator(RogueDispatcher dispatcher, RogueOptions options)
    {
        _dispatcher = dispatcher;
        _options = options;
    }

    /// <inheritdoc/>
    public ValueTask Send(ICommand command, CancellationToken cancellationToken = default)
        => _dispatcher.Send(command, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<TResponse> Send<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default)
        => _dispatcher.Send(command, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<TResponse> Send<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default)
        => _dispatcher.Send(query, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<object?> Send(object request, CancellationToken cancellationToken = default)
        => _dispatcher.SendObject(request, cancellationToken);

#if !NETSTANDARD2_0
    /// <inheritdoc/>
    public IAsyncEnumerable<TItem> CreateStream<TItem>(IStreamQuery<TItem> query, CancellationToken cancellationToken = default)
        => _dispatcher.CreateStream(query, cancellationToken);
#endif

    /// <inheritdoc/>
#if NETSTANDARD2_0
    public ValueTask<Unit> Publish(IEvent @event, CancellationToken cancellationToken = default)
        => _dispatcher.Publish(@event, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<Unit> Publish(object @event, CancellationToken cancellationToken = default)
    {
        if (@event is not IEvent e)
        {
            throw new ArgumentException($"Object of type '{@event?.GetType().FullName}' does not implement {nameof(IEvent)}.", nameof(@event));
        }
        return Publish(e, cancellationToken);
    }
#else
    public ValueTask Publish(IEvent @event, CancellationToken cancellationToken = default)
        => _dispatcher.Publish(@event, cancellationToken);

    /// <inheritdoc/>
    public ValueTask Publish(object @event, CancellationToken cancellationToken = default)
    {
        if (@event is not IEvent e)
        {
            throw new ArgumentException($"Object of type '{@event?.GetType().FullName}' does not implement {nameof(IEvent)}.", nameof(@event));
        }
        return Publish(e, cancellationToken);
    }
#endif
}
