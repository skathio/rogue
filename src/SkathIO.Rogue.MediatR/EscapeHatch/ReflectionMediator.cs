using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
#if !NETSTANDARD2_0
using System.Diagnostics.CodeAnalysis;
#endif
using Microsoft.Extensions.DependencyInjection;

namespace SkathIO.Rogue.Compatibility;

/// <summary>
/// Reflection-based mediator escape hatch for CQS message types the source generator cannot emit a
/// static dispatch path for (e.g. open-generic message types).
/// </summary>
/// <remarks>
/// This class is provided as an escape hatch for codebases that cannot immediately migrate all such
/// message types to the source-generator path. It uses runtime reflection to dispatch
/// commands/queries/events and is <b>not AOT-safe</b> — avoid in published AOT or trimmed applications.
/// Migrate callers to the generated <c>RogueDispatcher</c> or <c>ISender</c> path as soon as possible.
/// <para>
/// PD-46/PD-48: dispatches against the CQS core (PD-40 clean break) by <b>closed runtime type</b> — for
/// each call it constructs the closed handler interface from the message's concrete type and resolves it.
/// Commands resolve <c>ICommandHandler&lt;TCommand[, TResponse]&gt;</c>, queries
/// <c>IQueryHandler&lt;TQuery, TResponse&gt;</c>, events <c>IEventHandler&lt;TEvent&gt;</c>. Adapter
/// (<c>SkathIO.Rogue.Compatibility</c>) message types need no special handling here: under PD-48,
/// <c>INotification</c>/<c>IStreamRequest&lt;T&gt;</c> ARE core <c>IEvent</c>/<c>IStreamQuery&lt;T&gt;</c>
/// (IS-A), so <c>Publish(object)</c>'s <c>is IEvent</c> guard accepts an adapter notification directly;
/// adapter <c>IRequest&lt;T&gt;</c> command/query dispatch is the source generator's responsibility
/// (the F8 + <c>[MapAsQuery]</c> mapping), not this reflective fallback, whose object-dispatch entry
/// points (<c>Send(object)</c>) deliberately throw — use the generated object-dispatch path instead.
/// </para>
/// </remarks>
[Obsolete("Not AOT-safe — use the generator path (ISender / RogueDispatcher) for AOT-compatible requests. " +
          "See https://docs.skathio.io/rogue/escape-hatch")]
#if !NETSTANDARD2_0
[RequiresDynamicCode("ReflectionMediator uses MakeGenericType and MethodInfo.Invoke, which are not compatible with Native AOT.")]
[RequiresUnreferencedCode("ReflectionMediator uses reflection over ICommandHandler<,>/IQueryHandler<,>/IEventHandler<> which may be trimmed.")]
#endif
public sealed class ReflectionMediator : global::SkathIO.Rogue.IMediator
{
    private readonly IServiceProvider _serviceProvider;

    // Keyed on (message, response, isQuery): the same message type can be dispatched for different
    // TResponse, and command vs query resolve different handler interfaces.
    private static readonly ConcurrentDictionary<(Type message, Type response, bool isQuery), MethodInfo> _sendCache = new();

    public ReflectionMediator(IServiceProvider serviceProvider)
        => _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

    public ValueTask Send(global::SkathIO.Rogue.ICommand command, CancellationToken cancellationToken = default)
    {
        if (command is null)
        {
            throw new ArgumentNullException(nameof(command));
        }

        var commandType = command.GetType();
        var handlerType = typeof(global::SkathIO.Rogue.ICommandHandler<>).MakeGenericType(commandType);
        var handler = _serviceProvider.GetRequiredService(handlerType);
        var method = handlerType.GetMethod("Handle", BindingFlags.Instance | BindingFlags.Public)!;

#if NETSTANDARD2_0
        return IgnoreUnit((ValueTask<global::SkathIO.Rogue.Unit>)method.Invoke(handler, new object[] { command, cancellationToken })!);
#else
        return (ValueTask)method.Invoke(handler, new object[] { command, cancellationToken })!;
#endif
    }

    public ValueTask<TResponse> Send<TResponse>(
        global::SkathIO.Rogue.ICommand<TResponse> command,
        CancellationToken cancellationToken = default)
        => SendTyped<TResponse>(command, isQuery: false, cancellationToken);

    public ValueTask<TResponse> Send<TResponse>(
        global::SkathIO.Rogue.IQuery<TResponse> query,
        CancellationToken cancellationToken = default)
        => SendTyped<TResponse>(query, isQuery: true, cancellationToken);

    private ValueTask<TResponse> SendTyped<TResponse>(
        object? message, bool isQuery, CancellationToken cancellationToken)
    {
        if (message is null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        var messageType = message.GetType();
        var openHandlerType = isQuery
            ? typeof(global::SkathIO.Rogue.IQueryHandler<,>)
            : typeof(global::SkathIO.Rogue.ICommandHandler<,>);
        var handlerType = openHandlerType.MakeGenericType(messageType, typeof(TResponse));
        var handler = _serviceProvider.GetRequiredService(handlerType);

        var method = _sendCache.GetOrAdd((messageType, typeof(TResponse), isQuery), key =>
        {
            var ht = (key.isQuery
                ? typeof(global::SkathIO.Rogue.IQueryHandler<,>)
                : typeof(global::SkathIO.Rogue.ICommandHandler<,>)).MakeGenericType(key.message, key.response);
            return ht.GetMethod("Handle", BindingFlags.Instance | BindingFlags.Public)!;
        });

        return (ValueTask<TResponse>)method.Invoke(handler, new object[] { message, cancellationToken })!;
    }

    public ValueTask<object?> Send(object request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "Object-dispatch Send(object) is not supported via the reflection escape hatch. " +
            "Use the generated RogueDispatcher object-dispatch path (EnableObjectDispatch).");

#if NETSTANDARD2_0
    public ValueTask<global::SkathIO.Rogue.Unit> Publish(
        global::SkathIO.Rogue.IEvent @event,
        CancellationToken cancellationToken = default)
#else
    public ValueTask Publish(
        global::SkathIO.Rogue.IEvent @event,
        CancellationToken cancellationToken = default)
#endif
    {
        if (@event is null)
        {
            throw new ArgumentNullException(nameof(@event));
        }

        var eventType = @event.GetType();
        var handlerType = typeof(IEnumerable<>).MakeGenericType(
            typeof(global::SkathIO.Rogue.IEventHandler<>).MakeGenericType(eventType));

        var handlers = (IEnumerable<object>)_serviceProvider.GetRequiredService(handlerType);

        return PublishToHandlers(@event, handlers, cancellationToken);
    }

#if NETSTANDARD2_0
    public ValueTask<global::SkathIO.Rogue.Unit> Publish(object @event, CancellationToken cancellationToken = default)
#else
    public ValueTask Publish(object @event, CancellationToken cancellationToken = default)
#endif
    {
        if (@event is null)
        {
            throw new ArgumentNullException(nameof(@event));
        }

        if (@event is global::SkathIO.Rogue.IEvent typed)
        {
            return Publish(typed, cancellationToken);
        }

        throw new NotSupportedException(
            "Object-dispatch Publish(object) requires the payload to implement IEvent. " +
            "Use the generated RogueDispatcher object-dispatch path for non-IEvent payloads.");
    }

#if NETSTANDARD2_0
    private static async ValueTask<global::SkathIO.Rogue.Unit> PublishToHandlers(
        global::SkathIO.Rogue.IEvent @event,
        IEnumerable<object> handlers,
        CancellationToken cancellationToken)
    {
        // Resolve the MethodInfo once against the closed IEventHandler<T> interface type (not
        // handler.GetType()) — a concrete type implementing multiple IEventHandler<> specializations
        // would cause AmbiguousMatchException if resolved via GetType().
        var handleMethod = typeof(global::SkathIO.Rogue.IEventHandler<>)
            .MakeGenericType(@event.GetType())
            .GetMethod("Handle", BindingFlags.Instance | BindingFlags.Public)!;

        foreach (var handler in handlers)
        {
            await ((ValueTask<global::SkathIO.Rogue.Unit>)handleMethod.Invoke(handler, new object[] { @event, cancellationToken })!)
                .ConfigureAwait(false);
        }

        return global::SkathIO.Rogue.Unit.Value;
    }

    private static async ValueTask IgnoreUnit(ValueTask<global::SkathIO.Rogue.Unit> vt)
    {
        await vt.ConfigureAwait(false);
    }
#else
    private static async ValueTask PublishToHandlers(
        global::SkathIO.Rogue.IEvent @event,
        IEnumerable<object> handlers,
        CancellationToken cancellationToken)
    {
        // Resolve the MethodInfo once against the closed IEventHandler<T> interface type (not
        // handler.GetType()) — a concrete type implementing multiple IEventHandler<> specializations
        // would cause AmbiguousMatchException if resolved via GetType().
        var handleMethod = typeof(global::SkathIO.Rogue.IEventHandler<>)
            .MakeGenericType(@event.GetType())
            .GetMethod("Handle", BindingFlags.Instance | BindingFlags.Public)!;

        foreach (var handler in handlers)
        {
            await ((ValueTask)handleMethod.Invoke(handler, new object[] { @event, cancellationToken })!)
                .ConfigureAwait(false);
        }
    }
#endif

#if !NETSTANDARD2_0
    public IAsyncEnumerable<TItem> CreateStream<TItem>(
        global::SkathIO.Rogue.IStreamQuery<TItem> query,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "Streaming is not supported via the reflection escape hatch. " +
            "Use the generated RogueDispatcher.CreateStream path.");
#endif
}
