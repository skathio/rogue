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
    public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        var dispatcher = _serviceProvider.GetRequiredService<RogueDispatcher>();
        return dispatcher.Send(request, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<object?> Send(object request, CancellationToken cancellationToken = default)
    {
        var dispatcher = _serviceProvider.GetRequiredService<RogueDispatcher>();
        return dispatcher.SendObject(request, cancellationToken);
    }

#if !NETSTANDARD2_0
    /// <inheritdoc/>
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        var dispatcher = _serviceProvider.GetRequiredService<RogueDispatcher>();
        return dispatcher.CreateStream(request, cancellationToken);
    }
#endif

    /// <inheritdoc/>
#if NETSTANDARD2_0
    public ValueTask<Unit> Publish(INotification notification, CancellationToken cancellationToken = default)
    {
        var dispatcher = _serviceProvider.GetRequiredService<RogueDispatcher>();
        return dispatcher.Publish(notification, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<Unit> Publish(object notification, CancellationToken cancellationToken = default)
    {
        if (notification is not INotification n)
        {
            throw new ArgumentException($"Object of type '{notification?.GetType().FullName}' does not implement {nameof(INotification)}.", nameof(notification));
        }
        return Publish(n, cancellationToken);
    }
#else
    public ValueTask Publish(INotification notification, CancellationToken cancellationToken = default)
    {
        var dispatcher = _serviceProvider.GetRequiredService<RogueDispatcher>();
        return dispatcher.Publish(notification, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask Publish(object notification, CancellationToken cancellationToken = default)
    {
        if (notification is not INotification n)
        {
            throw new ArgumentException($"Object of type '{notification?.GetType().FullName}' does not implement {nameof(INotification)}.", nameof(notification));
        }
        return Publish(n, cancellationToken);
    }
#endif
}
