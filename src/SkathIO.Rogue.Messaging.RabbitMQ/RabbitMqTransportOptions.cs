using System;
using System.Collections.Generic;
using RabbitMQ.Client;

namespace SkathIO.Rogue.Messaging.RabbitMQ;

/// <summary>
/// Options for the RabbitMQ transport, set through <c>AddRogueRabbitMqTransport</c>. Messages are
/// published to a durable topic exchange, <see cref="Exchange"/>, with their stable name as the routing
/// key. A service that consumes sets <see cref="ServiceName"/>: it gets a durable queue of that name,
/// bound to the names of the events and commands it receives, and a dead-letter queue beside it.
/// </summary>
public sealed class RabbitMqTransportOptions
{
    /// <summary>The longest exchange, queue, routing key or header name, in characters (an AMQP short string).</summary>
    public const int MaxNameLength = 255;

    private readonly List<RoutingKeyMap> _routingKeyMaps = new List<RoutingKeyMap>();
    private readonly List<HeaderMap> _headerMaps = new List<HeaderMap>();
    private readonly List<Binding> _bindings = new List<Binding>();

    /// <summary>
    /// The broker, as an <c>amqp://</c> or <c>amqps://</c> URI with its credentials and virtual host.
    /// Never logged by this package.
    /// </summary>
    public Uri ConnectionUri { get; set; } = new Uri("amqp://localhost:5672/");

    /// <summary>The topic exchange every message is published to. Declared, durable, when the transport connects.</summary>
    public string Exchange { get; set; } = "rogue";

    /// <summary>
    /// The consuming service's queue, or null for a process that only publishes. Every instance of one
    /// service shares the queue, so each message reaches one of them. Its dead-letter queue is this name
    /// followed by <c>.dead-letter</c>.
    /// </summary>
    public string? ServiceName { get; set; }

    /// <summary>
    /// How many unsettled deliveries the broker sends this process ahead of the consumer, from 1 to
    /// 65535. It bounds the memory held by deliveries waiting for a handler.
    /// </summary>
    public int PrefetchCount { get; set; } = 100;

    /// <summary>
    /// Sets anything else on the connection factory, such as TLS or the recovery interval, after the
    /// transport's own settings: <see cref="ConnectionUri"/>, automatic recovery and topology recovery.
    /// </summary>
    public Action<ConnectionFactory>? ConfigureConnection { get; set; }

    /// <summary>The exchange messages are dead-lettered to: <see cref="Exchange"/> followed by <c>.dead-letter</c>.</summary>
    public string DeadLetterExchange => Exchange + ".dead-letter";

    /// <summary>The queue dead-lettered messages are kept in, or null when <see cref="ServiceName"/> is null.</summary>
    public string? DeadLetterQueue => ServiceName is null ? null : ServiceName + ".dead-letter";

    internal IReadOnlyList<RoutingKeyMap> RoutingKeyMaps => _routingKeyMaps;

    internal IReadOnlyList<HeaderMap> HeaderMaps => _headerMaps;

    internal IReadOnlyList<Binding> Bindings => _bindings;

    /// <summary>
    /// Receives JSON another system publishes to <paramref name="exchange"/> with exactly
    /// <paramref name="routingKey"/> as the message named <paramref name="messageName"/>. The service's
    /// queue is bound to the exchange with that key; the exchange must exist when the transport connects.
    /// </summary>
    /// <param name="exchange">The exchange the other system publishes to.</param>
    /// <param name="routingKey">The routing key it publishes with; no <c>*</c> or <c>#</c> wildcard.</param>
    /// <param name="messageName">The stable name of the Rogue message the JSON is read as.</param>
    /// <returns>These options.</returns>
    /// <exception cref="ArgumentException">An argument is not a valid name.</exception>
    public RabbitMqTransportOptions MapRoutingKey(string exchange, string routingKey, string messageName)
    {
        CheckExchange(exchange, nameof(exchange));
        CheckWord(routingKey, nameof(routingKey));
        if (routingKey.AsSpan().IndexOfAny('*', '#') >= 0)
        {
            throw new ArgumentException("A mapped routing key is matched exactly, so it cannot hold a '*' or '#' wildcard.", nameof(routingKey));
        }
        CheckMessageName(messageName, nameof(messageName));
        _routingKeyMaps.Add(new RoutingKeyMap(exchange, routingKey, messageName));
        return this;
    }

    /// <summary>
    /// Binds the service's queue to <paramref name="exchange"/>, another system's exchange, with
    /// <paramref name="bindingKey"/>, so what it publishes there reaches this service. Name those
    /// messages with <see cref="MapHeader"/>; one no map names is dead-lettered unread. The exchange must
    /// exist when the transport connects.
    /// </summary>
    /// <param name="exchange">The exchange the other system publishes to.</param>
    /// <param name="bindingKey">The binding key, which may hold <c>*</c> and <c>#</c> wildcards.</param>
    /// <returns>These options.</returns>
    /// <exception cref="ArgumentException">An argument is not a valid name.</exception>
    public RabbitMqTransportOptions Bind(string exchange, string bindingKey)
    {
        CheckExchange(exchange, nameof(exchange));
        CheckWord(bindingKey, nameof(bindingKey));
        _bindings.Add(new Binding(exchange, bindingKey));
        return this;
    }

    /// <summary>
    /// Receives JSON whose <paramref name="header"/> header is the string <paramref name="value"/> as the
    /// message named <paramref name="messageName"/>, whichever exchange it came through. The first header
    /// map that matches wins, before any routing-key map; bring the messages to the service's queue with
    /// <see cref="Bind"/> or <see cref="MapRoutingKey"/>.
    /// </summary>
    /// <param name="header">The header that names the message type in the other system.</param>
    /// <param name="value">The header's value for this message type, compared ordinally.</param>
    /// <param name="messageName">The stable name of the Rogue message the JSON is read as.</param>
    /// <returns>These options.</returns>
    /// <exception cref="ArgumentException">An argument is not a valid name.</exception>
    public RabbitMqTransportOptions MapHeader(string header, string value, string messageName)
    {
        CheckWord(header, nameof(header));
        CheckWord(value, nameof(value));
        CheckMessageName(messageName, nameof(messageName));
        _headerMaps.Add(new HeaderMap(header, value, messageName));
        return this;
    }

    /// <summary>Checks every option and throws on the first that is out of range.</summary>
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(ConnectionUri, nameof(ConnectionUri));
        if (!ConnectionUri.IsAbsoluteUri || (ConnectionUri.Scheme != "amqp" && ConnectionUri.Scheme != "amqps"))
        {
            throw new ArgumentException("The connection URI must be an absolute amqp:// or amqps:// URI.", nameof(ConnectionUri));
        }
        CheckExchange(Exchange, nameof(Exchange));
        if (DeadLetterExchange.Length > MaxNameLength)
        {
            throw new ArgumentException("The exchange name leaves no room for its '.dead-letter' exchange.", nameof(Exchange));
        }
        if (ServiceName is not null)
        {
            CheckName(ServiceName, nameof(ServiceName));
            if (DeadLetterQueue!.Length > MaxNameLength)
            {
                throw new ArgumentException("The service name leaves no room for its '.dead-letter' queue.", nameof(ServiceName));
            }
            if (ServiceName.StartsWith("amq.", StringComparison.Ordinal))
            {
                throw new ArgumentException("Queue names starting with 'amq.' are reserved by the broker.", nameof(ServiceName));
            }
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(PrefetchCount, 1, nameof(PrefetchCount));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(PrefetchCount, ushort.MaxValue, nameof(PrefetchCount));
    }

    // The transport keeps a copy, so changing the options after it is built changes nothing.
    internal RabbitMqTransportOptions Copy()
    {
        var copy = new RabbitMqTransportOptions
        {
            ConnectionUri = ConnectionUri,
            Exchange = Exchange,
            ServiceName = ServiceName,
            PrefetchCount = PrefetchCount,
            ConfigureConnection = ConfigureConnection,
        };
        copy._routingKeyMaps.AddRange(_routingKeyMaps);
        copy._headerMaps.AddRange(_headerMaps);
        copy._bindings.AddRange(_bindings);
        return copy;
    }

    private static void CheckExchange(string exchange, string parameter)
    {
        CheckName(exchange, parameter);
        if (exchange.StartsWith("amq.", StringComparison.Ordinal))
        {
            throw new ArgumentException("Exchange names starting with 'amq.' are reserved by the broker.", parameter);
        }
    }

    // Exchange and queue names: the characters the broker accepts in a name without quoting.
    private static void CheckName(string? name, string parameter)
    {
        CheckWord(name, parameter);
        foreach (char c in name!)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-' or ':'))
            {
                throw new ArgumentException("A name may hold only ASCII letters, digits, '.', '_', '-' and ':'.", parameter);
            }
        }
    }

    // Routing keys, header names and values: printable ASCII, so nothing on the wire is ambiguous.
    private static void CheckWord(string? word, string parameter)
    {
        ArgumentNullException.ThrowIfNull(word, parameter);
        if (word.Length == 0 || word.Length > MaxNameLength)
        {
            throw new ArgumentException("The value must be 1 to " + MaxNameLength + " characters.", parameter);
        }
        foreach (char c in word)
        {
            if (c < 0x21 || c > 0x7E)
            {
                throw new ArgumentException("The value may hold only printable ASCII characters, without spaces.", parameter);
            }
        }
    }

    private static void CheckMessageName(string messageName, string parameter)
    {
        ArgumentNullException.ThrowIfNull(messageName, parameter);
        if (!MessageLimits.IsValidName(messageName))
        {
            throw new ArgumentException("'" + messageName + "' is not a valid message name.", parameter);
        }
    }

    internal sealed record RoutingKeyMap(string Exchange, string RoutingKey, string MessageName);

    internal sealed record HeaderMap(string Header, string Value, string MessageName);

    internal sealed record Binding(string Exchange, string BindingKey);
}
