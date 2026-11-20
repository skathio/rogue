using System;
using System.Collections.Generic;
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
    public const string DeliveryCountHeader = "rogue-delivery-count";
    public const string DeadLetterReasonHeader = "rogue-dead-letter-reason";

    /// <summary>
    /// The longest header string read from a delivery, in bytes; a longer one is ignored, so a sender
    /// cannot make every message carry a large id into logs and telemetry.
    /// </summary>
    public const int MaxHeaderBytes = 1024;

    /// <summary>The highest delivery count read from a header; a higher one is read as this.</summary>
    public const int MaxDeliveryCount = 1_000_000;

    /// <summary>The persistent properties <paramref name="envelope"/> is published with.</summary>
    public static BasicProperties ToProperties(MessageEnvelope envelope, int? deliveryCount = null, string? deadLetterReason = null)
    {
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal);
        AddIfSet(headers, CausationHeader, envelope.CausationId);
        AddIfSet(headers, TraceParentHeader, envelope.TraceParent);
        AddIfSet(headers, DeadLetterReasonHeader, deadLetterReason);
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
    /// The envelope of <paramref name="delivery"/>, named by <see cref="ResolveName"/>. The body is
    /// copied, since the client reuses its buffer once the delivery event returns.
    /// </summary>
    public static MessageEnvelope ToEnvelope(RabbitMqTransportOptions options, BasicDeliverEventArgs delivery)
    {
        IReadOnlyBasicProperties properties = delivery.BasicProperties;
        var envelope = new MessageEnvelope
        {
            Name = ResolveName(options, delivery.Exchange, delivery.RoutingKey, properties),
            ContentType = properties.ContentType ?? string.Empty,
            CorrelationId = properties.CorrelationId,
            CausationId = HeaderString(properties, CausationHeader),
            TraceParent = HeaderString(properties, TraceParentHeader),
            Body = delivery.Body.ToArray(),
        };
        if (!string.IsNullOrEmpty(properties.MessageId))
        {
            envelope.Id = properties.MessageId;
        }
        return envelope;
    }

    /// <summary>
    /// The stable name of a delivery: from the first header map whose header holds its value, else from
    /// a routing-key map for the exchange and key, else the routing key when the delivery came through
    /// the transport's own exchange, else, on the default exchange only, the AMQP type property, which
    /// names the copies the transport sends back there. Empty when none applies, which the consumer
    /// dead-letters as an unknown name.
    /// </summary>
    public static string ResolveName(RabbitMqTransportOptions options, string exchange, string routingKey, IReadOnlyBasicProperties properties)
    {
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
        // On the own exchange the routing key is what the queue was bound by, so the sender's type
        // property cannot name it something else and reach a handler the binding never let through.
        if (string.Equals(exchange, options.Exchange, StringComparison.Ordinal))
        {
            return routingKey;
        }
        // On another system's exchange only a map names a delivery: its sender's type property could
        // otherwise pick any message name and reach a handler no map lets through.
        if (exchange.Length != 0 || string.IsNullOrEmpty(properties.Type))
        {
            return string.Empty;
        }
        return properties.Type;
    }

    /// <summary>
    /// How many times a delivery has been delivered: the count the transport stamped when it sent the
    /// message back for a retry (1 when there is none or it is not a positive whole number), one more
    /// when the broker marks it redelivered after a delivery that was never settled.
    /// </summary>
    public static int DeliveryCount(IReadOnlyBasicProperties properties, bool redelivered)
    {
        long count = 1;
        if (properties.Headers is { } headers && headers.TryGetValue(DeliveryCountHeader, out object? value))
        {
            count = value switch
            {
                int i => i,
                long l => l,
                short s => s,
                byte b => b,
                _ => 1,
            };
        }
        count = Math.Clamp(count, 1, MaxDeliveryCount);
        return (int)count + (redelivered ? 1 : 0);
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
