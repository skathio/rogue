using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace SkathIO.Rogue.Messaging.RabbitMQ;

/// <summary>
/// How an envelope travels as an AMQP message. The id, name, content type and correlation id go in the
/// standard properties (message id, type, content type, correlation id); the rest in headers. Everything
/// read from a delivery is untrusted, and the name it gets comes from the transport's maps before
/// anything the sender claims.
/// </summary>
internal static class RabbitMqWire
{
    public const string CausationHeader = "rogue-causation-id";
    public const string TraceParentHeader = "traceparent";
    public const string OriginHeader = "rogue-origin";
    /// <summary>
    /// The delivery count stamped on a copy the transport sends back, for whoever reads the queue. It is
    /// never read back: the sender controls every header, so the count comes from <see cref="DeliveryStampHeader"/>.
    /// </summary>
    public const string DeliveryCountHeader = "rogue-delivery-count";

    /// <summary>The count of a copy the transport sends back, signed by it; see <see cref="DeliveryStamp"/>.</summary>
    public const string DeliveryStampHeader = "rogue-delivery-stamp";
    public const string DeadLetterReasonHeader = "rogue-dead-letter-reason";

    /// <summary>
    /// The header a quorum queue sets on a delivery it returns to the queue unsettled: how many times it
    /// has been returned so far.
    /// </summary>
    public const string BrokerDeliveryCountHeader = "x-delivery-count";

    /// <summary>
    /// The longest header string read from a delivery, in bytes; a longer one is ignored, so a sender
    /// cannot make every message carry a large id into logs and telemetry.
    /// </summary>
    public const int MaxHeaderBytes = 1024;

    /// <summary>The highest signed delivery count a delivery is given; a higher one is read as this.</summary>
    public const int MaxDeliveryCount = 1_000_000;

    /// <summary>The persistent properties <paramref name="envelope"/> is published with.</summary>
    public static BasicProperties ToProperties(MessageEnvelope envelope, int? deliveryCount = null, string? deadLetterReason = null, string? deliveryStamp = null)
    {
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal);
        AddIfSet(headers, CausationHeader, envelope.CausationId);
        AddIfSet(headers, TraceParentHeader, envelope.TraceParent);
        AddIfSet(headers, OriginHeader, envelope.Origin);
        AddIfSet(headers, DeadLetterReasonHeader, deadLetterReason);
        AddIfSet(headers, DeliveryStampHeader, deliveryStamp);
        if (deliveryCount is int count)
        {
            headers[DeliveryCountHeader] = count;
        }
        return new BasicProperties
        {
            MessageId = envelope.Id,
            Type = envelope.Name,
            ContentType = envelope.ContentType,
            CorrelationId = envelope.CorrelationId,
            DeliveryMode = DeliveryModes.Persistent,
            Headers = headers,
        };
    }

    /// <summary>
    /// The envelope of <paramref name="delivery"/>, named by <see cref="ResolveName"/>. On the default
    /// exchange a name that is not one of <paramref name="knownNames"/> is replaced by the empty name,
    /// which the consumer dead-letters: anyone allowed to publish to the default exchange can put a
    /// message on the service's queue, so its type property may name only a message the service
    /// receives anyway, and a copy the transport sent back always carries one of those. The transport
    /// always passes its known names; null, for a reader that has none, keeps any name. The body is
    /// copied, since the client reuses its buffer once the delivery event returns.
    /// </summary>
    /// <param name="options">The transport's options, with its exchange and maps.</param>
    /// <param name="delivery">The delivery as the client handed it over.</param>
    /// <param name="knownNames">The names the service receives: its subscriptions and every mapped name.</param>
    public static MessageEnvelope ToEnvelope(RabbitMqTransportOptions options, BasicDeliverEventArgs delivery, IReadOnlySet<string>? knownNames = null)
    {
        IReadOnlyBasicProperties properties = delivery.BasicProperties;
        string name = ResolveName(options, delivery.Exchange, delivery.RoutingKey, properties);
        if (knownNames is not null && delivery.Exchange.Length == 0 && !knownNames.Contains(name))
        {
            name = string.Empty;
        }
        var envelope = new MessageEnvelope
        {
            Name = name,
            ContentType = properties.ContentType ?? string.Empty,
            CorrelationId = properties.CorrelationId,
            CausationId = HeaderString(properties, CausationHeader),
            TraceParent = HeaderString(properties, TraceParentHeader),
            Origin = HeaderString(properties, OriginHeader),
            Body = delivery.Body.ToArray(),
        };
        if (!string.IsNullOrEmpty(properties.MessageId))
        {
            envelope.Id = properties.MessageId;
        }
        return envelope;
    }

    /// <summary>
    /// The stable name of a delivery: the routing key when the delivery came through the transport's own
    /// exchange, else from the first header map whose header holds its value, else from a routing-key map
    /// for the exchange and key, else, on the default exchange only, the AMQP type property, which names
    /// the copies the transport sends back there. Empty when none applies, which the consumer
    /// dead-letters as an unknown name. <see cref="ToEnvelope"/> also holds a default-exchange name to
    /// the names the service receives.
    /// </summary>
    public static string ResolveName(RabbitMqTransportOptions options, string exchange, string routingKey, IReadOnlyBasicProperties properties)
    {
        // On the own exchange the routing key is what the queue was bound by, so neither a header the
        // sender sets nor its type property can name it something else and reach a handler the binding
        // never let through.
        if (string.Equals(exchange, options.Exchange, StringComparison.Ordinal))
        {
            return routingKey;
        }
        foreach (RabbitMqTransportOptions.HeaderMap map in options.HeaderMaps)
        {
            if (string.Equals(HeaderString(properties, map.Header), map.Value, StringComparison.Ordinal))
            {
                return map.MessageName;
            }
        }
        foreach (RabbitMqTransportOptions.RoutingKeyMap map in options.RoutingKeyMaps)
        {
            if (string.Equals(map.Exchange, exchange, StringComparison.Ordinal) && string.Equals(map.RoutingKey, routingKey, StringComparison.Ordinal))
            {
                return map.MessageName;
            }
        }
        // On another system's exchange only a map names a delivery: its sender's type property could
        // otherwise pick any message name and reach a handler no map lets through.
        if (exchange.Length != 0 || string.IsNullOrEmpty(properties.Type))
        {
            return string.Empty;
        }
        return properties.Type;
    }

    /// <summary>The names a delivery may get on the default exchange: <paramref name="subscriptions"/> and every name a map gives.</summary>
    public static HashSet<string> KnownNames(RabbitMqTransportOptions options, IEnumerable<string> subscriptions)
    {
        var known = new HashSet<string>(subscriptions, StringComparer.Ordinal);
        known.UnionWith(options.HeaderMaps.Select(static map => map.MessageName));
        known.UnionWith(options.RoutingKeyMaps.Select(static map => map.MessageName));
        return known;
    }

    /// <summary>A header's value as a string, when it is one of at most <see cref="MaxHeaderBytes"/> UTF-8 bytes; otherwise null.</summary>
    public static string? HeaderString(IReadOnlyBasicProperties properties, string header)
    {
        if (properties.Headers is not { } headers || !headers.TryGetValue(header, out object? value))
        {
            return null;
        }
        // The client reads AMQP long strings as bytes; a header set in this process is still a string.
        switch (value)
        {
            case byte[] bytes when bytes.Length <= MaxHeaderBytes:
                try
                {
                    return new UTF8Encoding(false, true).GetString(bytes);
                }
                catch (ArgumentException)
                {
                    return null;
                }
            case string text when Encoding.UTF8.GetByteCount(text) <= MaxHeaderBytes:
                return text;
            default:
                return null;
        }
    }

    private static void AddIfSet(Dictionary<string, object?> headers, string header, string? value)
    {
        if (value is not null)
        {
            headers[header] = value;
        }
    }
}
