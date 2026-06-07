using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace SkathIO.Rogue.Compatibility;

/// <summary>
/// Reflection-based mediator for open-generic request types not supported by the source generator.
/// </summary>
/// <remarks>
/// This class is provided as an escape hatch for codebases that cannot immediately migrate all
/// open-generic request types to the source-generator path. It uses runtime reflection to dispatch
/// requests and is <b>not AOT-safe</b> — avoid in published AOT or trimmed applications.
/// Migrate callers to the generated <c>RogueDispatcher</c> or <c>ISender</c> path as soon as possible.
/// </remarks>
[Obsolete("Not AOT-safe — use the generator path (ISender / RogueDispatcher) for AOT-compatible requests. " +
          "See https://docs.skathio.io/rogue/escape-hatch")]
public sealed class ReflectionMediator : global::SkathIO.Rogue.IMediator
{
    private readonly IServiceProvider _serviceProvider;

    // Keyed on (request, response): the same request type can be dispatched for different TResponse
    // (open-generic requests), and the resolved Handle MethodInfo differs per response type.
    private static readonly ConcurrentDictionary<(Type request, Type response), MethodInfo> _sendCache = new();

    public ReflectionMediator(IServiceProvider serviceProvider)
        => _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

    public ValueTask<TResponse> Send<TResponse>(
        global::SkathIO.Rogue.IRequest<TResponse> request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var requestType = request.GetType();
        var handlerType = typeof(global::SkathIO.Rogue.IRequestHandler<,>).MakeGenericType(requestType, typeof(TResponse));
        var handler = _serviceProvider.GetRequiredService(handlerType);

        var method = _sendCache.GetOrAdd((requestType, typeof(TResponse)), static key =>
        {
            var ht = typeof(global::SkathIO.Rogue.IRequestHandler<,>).MakeGenericType(key.request, key.response);
            return ht.GetMethod("Handle", BindingFlags.Instance | BindingFlags.Public)!;
        });

        return (ValueTask<TResponse>)method.Invoke(handler, new object[] { request, cancellationToken })!;
    }

    public ValueTask<object?> Send(object request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "Object-dispatch Send(object) is not supported via the reflection escape hatch. " +
            "Use the generated RogueDispatcher object-dispatch path (EnableObjectDispatch).");

#if NETSTANDARD2_0
    public ValueTask<global::SkathIO.Rogue.Unit> Publish(
        global::SkathIO.Rogue.INotification notification,
        CancellationToken cancellationToken = default)
#else
    public ValueTask Publish(
        global::SkathIO.Rogue.INotification notification,
        CancellationToken cancellationToken = default)
#endif
    {
        if (notification is null)
        {
            throw new ArgumentNullException(nameof(notification));
        }

        var notificationType = notification.GetType();
        var handlerType = typeof(IEnumerable<>).MakeGenericType(
            typeof(global::SkathIO.Rogue.INotificationHandler<>).MakeGenericType(notificationType));

        var handlers = (IEnumerable<object>)_serviceProvider.GetRequiredService(handlerType);

        return PublishToHandlers(notification, handlers, cancellationToken);
    }

#if NETSTANDARD2_0
    public ValueTask<global::SkathIO.Rogue.Unit> Publish(object notification, CancellationToken cancellationToken = default)
#else
    public ValueTask Publish(object notification, CancellationToken cancellationToken = default)
#endif
    {
        if (notification is null)
        {
            throw new ArgumentNullException(nameof(notification));
        }

        if (notification is global::SkathIO.Rogue.INotification typed)
        {
            return Publish(typed, cancellationToken);
        }

        throw new NotSupportedException(
            "Object-dispatch Publish(object) requires the notification to implement INotification. " +
            "Use the generated RogueDispatcher object-dispatch path for non-INotification payloads.");
    }

#if NETSTANDARD2_0
    private static async ValueTask<global::SkathIO.Rogue.Unit> PublishToHandlers(
        global::SkathIO.Rogue.INotification notification,
        IEnumerable<object> handlers,
        CancellationToken cancellationToken)
    {
        // Resolve the MethodInfo once against the closed INotificationHandler<T> interface type (not
        // handler.GetType()) — a concrete type implementing multiple INotificationHandler<> specializations
        // would cause AmbiguousMatchException if resolved via GetType().
        var handleMethod = typeof(global::SkathIO.Rogue.INotificationHandler<>)
            .MakeGenericType(notification.GetType())
            .GetMethod("Handle", BindingFlags.Instance | BindingFlags.Public)!;

        foreach (var handler in handlers)
        {
            await ((ValueTask<global::SkathIO.Rogue.Unit>)handleMethod.Invoke(handler, new object[] { notification, cancellationToken })!)
                .ConfigureAwait(false);
        }

        return global::SkathIO.Rogue.Unit.Value;
    }
#else
    private static async ValueTask PublishToHandlers(
        global::SkathIO.Rogue.INotification notification,
        IEnumerable<object> handlers,
        CancellationToken cancellationToken)
    {
        // Resolve the MethodInfo once against the closed INotificationHandler<T> interface type (not
        // handler.GetType()) — a concrete type implementing multiple INotificationHandler<> specializations
        // would cause AmbiguousMatchException if resolved via GetType().
        var handleMethod = typeof(global::SkathIO.Rogue.INotificationHandler<>)
            .MakeGenericType(notification.GetType())
            .GetMethod("Handle", BindingFlags.Instance | BindingFlags.Public)!;

        foreach (var handler in handlers)
        {
            await ((ValueTask)handleMethod.Invoke(handler, new object[] { notification, cancellationToken })!)
                .ConfigureAwait(false);
        }
    }
#endif

#if !NETSTANDARD2_0
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
        global::SkathIO.Rogue.IStreamRequest<TResponse> request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "Streaming is not supported via the reflection escape hatch. " +
            "Use the generated RogueDispatcher.CreateStream path.");
#endif
}
