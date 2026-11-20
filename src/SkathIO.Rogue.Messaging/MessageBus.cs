using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace SkathIO.Rogue.Messaging;

/// <summary>The <see cref="IMessageBus"/> over the registered transport.</summary>
internal sealed class MessageBus : IMessageBus
{
    private readonly IServiceProvider _services;
    private MessageRegistry? _registry;
    private IMessageTransport? _transport;

    // Built only by AddRogueMessaging. The registry and transport are resolved on first use, so a
    // misconfiguration fails the first publish rather than the construction of every dispatcher.
    internal MessageBus(IServiceProvider services) => _services = services;

    internal MessageRegistry Registry => _registry ??= _services.GetRequiredService<MessageRegistry>();

    private IMessageTransport Transport => _transport ??= _services.GetRequiredService<IMessageTransport>();

    public ValueTask Publish<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        where TMessage : notnull
        => Send(BindingOf(message), message, cancellationToken);

    public MessageEnvelope CreateEnvelope<TMessage>(TMessage message)
        where TMessage : notnull
    {
        BoundMessage bound = BindingOf(message);
        byte[] body = Serialize(bound, message);
        using Activity? activity = StartPublish(bound);
        return Envelope(bound, body, activity);
    }

    /// <summary>The binding to forward <paramref name="event"/> with, or null when it is not sent to the bus.</summary>
    internal BoundMessage? ForwardingFor(IEvent @event)
    {
        if (!Registry.TryGet(@event.GetType(), out BoundMessage? bound))
        {
            return null;
        }
        // An inbound event published to its handlers is not sent back to where it came from.
        return ReferenceEquals(InboundContext.Current?.Message, @event) ? null : bound;
    }

    /// <summary>Sends <paramref name="event"/> once its local handlers have run, unless they fail.</summary>
    internal async ValueTask ForwardAfterAsync(BoundMessage bound, IEvent @event, ValueTask localHandlers, CancellationToken cancellationToken)
    {
        await localHandlers.ConfigureAwait(false);
        await Send(bound, @event, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask Send(BoundMessage bound, object message, CancellationToken cancellationToken)
    {
        byte[] body = Serialize(bound, message);
        using Activity? activity = StartPublish(bound);
        MessageEnvelope envelope = Envelope(bound, body, activity);
        await Transport.SendAsync(new[] { envelope }, cancellationToken).ConfigureAwait(false);
        RogueMessagingTelemetry.Published.Add(1, new TagList { { RogueMessagingTelemetry.NameTag, bound.Name } });
    }

    // The envelope of a message with this body: a new id, the ids of the inbound message being handled,
    // if any, and the trace context of the publish activity or, without one, the current activity.
    private static MessageEnvelope Envelope(BoundMessage bound, byte[] body, Activity? activity)
    {
        Activity? trace = activity ?? Activity.Current;
        var envelope = new MessageEnvelope
        {
            Name = bound.Name,
            ContentType = MessageEnvelope.JsonContentType,
            Body = body,
            TraceParent = trace is { IdFormat: ActivityIdFormat.W3C } ? trace.Id : null,
        };
        MessageEnvelope? cause = InboundContext.Current?.Envelope;
        envelope.CorrelationId = cause is null ? envelope.Id : cause.CorrelationId ?? cause.Id;
        envelope.CausationId = cause?.Id;
        return envelope;
    }

    private static Activity? StartPublish(BoundMessage bound)
    {
        Activity? activity = RogueMessagingTelemetry.Source.StartActivity(RogueMessagingTelemetry.PublishActivity, ActivityKind.Producer);
        activity?.SetTag(RogueMessagingTelemetry.NameTag, bound.Name);
        return activity;
    }

    private BoundMessage BindingOf<TMessage>(TMessage message)
        where TMessage : notnull
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!Registry.TryGet(message.GetType(), out BoundMessage? bound))
        {
            throw new InvalidOperationException(message.GetType() + " is not a message. Mark it [Message(\"name\")] in a project that references SkathIO.Rogue.Messaging, and add [JsonSerializable(typeof(" + message.GetType().Name + "))] to MessagingOptions.SerializerContext.");
        }
        return bound;
    }

    // The body, refused when it is over the size limit, so nothing too large is sent or stored.
    private byte[] Serialize(BoundMessage bound, object message)
    {
        byte[] body = bound.Serialize(message);
        int limit = Registry.Options.MaxMessageBytes;
        if (body.Length > limit)
        {
            throw new InvalidOperationException("The '" + bound.Name + "' message is " + body.Length + " bytes, over MessagingOptions.MaxMessageBytes (" + limit + "). Nothing was sent.");
        }
        return body;
    }
}
