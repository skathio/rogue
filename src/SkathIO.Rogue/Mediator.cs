using System;
#if !NETSTANDARD2_0
using System.Collections.Generic;
#endif
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace SkathIO.Rogue;

/// <summary>
/// Default mediator implementation. Delegates to the source-generated dispatcher.
/// Prefer injecting <see cref="ISender"/> or <see cref="IPublisher"/> over this type directly.
/// </summary>
public sealed class Mediator : IMediator
{
    private readonly IServiceProvider _serviceProvider;
    private readonly RogueOptions _options;

    /// <summary>Initializes the mediator.</summary>
    public Mediator(IServiceProvider serviceProvider, RogueOptions options)
    {
        _serviceProvider = serviceProvider;
        _options = options;
    }

    /// <inheritdoc/>
    public ValueTask Send(ICommand command, CancellationToken cancellationToken = default)
    {
        var dispatcher = _serviceProvider.GetRequiredService<RogueDispatcher>();
        return dispatcher.Send(command, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<TResponse> Send<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default)
    {
        var dispatcher = _serviceProvider.GetRequiredService<RogueDispatcher>();
        return dispatcher.Send(command, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<TResponse> Send<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default)
    {
        var dispatcher = _serviceProvider.GetRequiredService<RogueDispatcher>();
        return dispatcher.Send(query, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<object?> Send(object request, CancellationToken cancellationToken = default)
    {
        var dispatcher = _serviceProvider.GetRequiredService<RogueDispatcher>();
        return dispatcher.SendObject(request, cancellationToken);
    }

#if !NETSTANDARD2_0
    /// <inheritdoc/>
    public IAsyncEnumerable<TItem> CreateStream<TItem>(IStreamQuery<TItem> query, CancellationToken cancellationToken = default)
    {
        var dispatcher = _serviceProvider.GetRequiredService<RogueDispatcher>();
        return dispatcher.CreateStream(query, cancellationToken);
    }
#endif

    /// <inheritdoc/>
#if NETSTANDARD2_0
    public ValueTask<Unit> Publish(IEvent @event, CancellationToken cancellationToken = default)
    {
        var dispatcher = _serviceProvider.GetRequiredService<RogueDispatcher>();
        return dispatcher.Publish(@event, cancellationToken);
    }

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
    {
        var dispatcher = _serviceProvider.GetRequiredService<RogueDispatcher>();
        return dispatcher.Publish(@event, cancellationToken);
    }

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
