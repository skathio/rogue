using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace SkathIO.Rogue.Messaging.RabbitMQ;

/// <summary>
/// The message transport over RabbitMQ. It publishes each message, persistent, to a durable topic
/// exchange with its name as the routing key, and waits for the broker's publisher confirm. A consuming
/// service gets a durable quorum queue, bound to the names it handles, that dead-letters to a queue of
/// its own; the broker sends at most <see cref="RabbitMqTransportOptions.PrefetchCount"/> deliveries
/// ahead, and each is acked only when the consumer settles it as handled.
/// </summary>
/// <remarks>
/// <para>
/// The connection is opened on first use, and opened again on the next use when that fails. Once open,
/// the client reconnects by itself after a network failure or a broker restart, and declares the
/// exchanges, queues, bindings and consumer again. Deliveries not settled when a connection is lost
/// are delivered again by the broker; the transport drops its copies of them.
/// </para>
/// <para>
/// RabbitMQ has no native delay, so the consumer waits out a retry delay itself. A message nacked for a
/// retry is published back to the service's queue, then acked, its delivery count one higher signed under
/// the shared <see cref="RabbitMqTransportOptions.DeliveryStampKey"/>, so a sender cannot set it; a dead-lettered
/// one is published to the dead-letter queue with the reason in its <c>rogue-dead-letter-reason</c> header, then acked.
/// Each copy is confirmed by the broker before the original is acked, so a message is delivered at
/// least once; when a copy cannot be published, the original is handed back to the broker (requeued,
/// or dead-lettered without a reason), except a deferred one, which this consumer receives again,
/// still unsettled, so that being deferred never costs it an attempt.
/// </para>
/// </remarks>
public sealed partial class RabbitMqTransport : IMessageTransport, IAsyncDisposable, IDisposable
{
    private const string QueueTypeArgument = "x-queue-type";
    private const string DeadLetterExchangeArgument = "x-dead-letter-exchange";
    private const string DeadLetterRoutingKeyArgument = "x-dead-letter-routing-key";

    // How long a receive waits for the first delivery before it returns none.
    private static readonly TimeSpan _receiveWait = TimeSpan.FromSeconds(1);

    private readonly RabbitMqTransportOptions _options;
    private readonly string[] _subscriptions;
    private readonly HashSet<string> _knownNames;
    private readonly SemaphoreSlim _connectGate = new SemaphoreSlim(1, 1);
    private readonly SemaphoreSlim _publishGate = new SemaphoreSlim(1, 1);
    private readonly SemaphoreSlim _settleGate = new SemaphoreSlim(1, 1);
    private readonly Channel<Delivery> _deliveries = Channel.CreateUnbounded<Delivery>(new UnboundedChannelOptions { SingleWriter = false });
    private readonly DeliveryStamp _stamp;
    private IConnection? _connection;
    private IChannel? _publisher;
    private IChannel? _consumer;
    private int _generation;
    private bool _disposed;

    /// <summary>Creates the transport; it connects on first use.</summary>
    /// <param name="options">The broker and topology; copied, so later changes have no effect.</param>
    /// <param name="subscriptions">The names of the messages this service receives; its queue is bound to each.</param>
    /// <exception cref="ArgumentException">An option or a name is not valid.</exception>
    public RabbitMqTransport(RabbitMqTransportOptions options, IEnumerable<string> subscriptions)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(subscriptions);
        _options = options.Copy();
        _options.Validate();
        _stamp = new DeliveryStamp(_options.DeliveryStampKey!);
        _subscriptions = subscriptions.Distinct(StringComparer.Ordinal).ToArray();
        foreach (string name in _subscriptions)
        {
            if (!MessageLimits.IsValidName(name))
            {
                throw new ArgumentException("'" + name + "' is not a valid message name.", nameof(subscriptions));
            }
        }
        _knownNames = RabbitMqWire.KnownNames(_options, _subscriptions);
    }

    /// <inheritdoc />
    public TransportCapabilities Capabilities => TransportCapabilities.NativeDeadLetter | TransportCapabilities.BatchSend;

    /// <summary>The <see cref="RabbitMqTransportOptions.ServiceName"/>, which also names the service's queue.</summary>
    public string? ServiceName => _options.ServiceName;

    /// <summary>The <see cref="RabbitMqTransportOptions.PrefetchCount"/>: the most unsettled deliveries the broker sends.</summary>
    public int? UnsettledLimit => _options.PrefetchCount;

    internal IReadOnlyList<string> Subscriptions => _subscriptions;

    /// <summary>The names a delivery's type property may give it on the default exchange: the subscriptions and every mapped name.</summary>
    internal IReadOnlySet<string> KnownNames => _knownNames;

    /// <summary>
    /// The largest body a delivery may have, in bytes; a larger one is nacked to the queue's dead-letter
    /// exchange as it arrives, before its body is copied. Registration sets it from the messaging options.
    /// </summary>
    internal int MaxMessageBytes { get; init; } = int.MaxValue;

    internal RabbitMqTransportOptions Options => _options;

    /// <inheritdoc />
    /// <exception cref="ArgumentException">A message's name is not a valid message name.</exception>
    /// <exception cref="ObjectDisposedException">The transport is disposed.</exception>
    public async ValueTask SendAsync(IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        foreach (MessageEnvelope envelope in messages)
        {
            ArgumentNullException.ThrowIfNull(envelope, nameof(messages));
            if (!MessageLimits.IsValidName(envelope.Name))
            {
                throw new ArgumentException("A message's name is its routing key, and must be a valid message name.", nameof(messages));
            }
        }
        if (messages.Count == 0)
        {
            return;
        }
        IChannel publisher = await PublisherAsync(cancellationToken).ConfigureAwait(false);
        await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Published back to back and confirmed together: one wait for the broker per batch.
            var confirms = new Task[messages.Count];
            for (int i = 0; i < messages.Count; i++)
            {
                MessageEnvelope envelope = messages[i];
                confirms[i] = publisher.BasicPublishAsync(_options.Exchange, envelope.Name, false, RabbitMqWire.ToProperties(envelope), envelope.Body, cancellationToken).AsTask();
            }
            await Task.WhenAll(confirms).ConfigureAwait(false);
        }
        finally
        {
            _publishGate.Release();
        }
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException"><see cref="RabbitMqTransportOptions.ServiceName"/> is not set, so there is no queue to receive from.</exception>
    /// <exception cref="ObjectDisposedException">The transport is disposed.</exception>
    public async ValueTask<IReadOnlyList<ReceivedMessage>> ReceiveAsync(int maxCount, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        if (_options.ServiceName is null)
        {
            throw new InvalidOperationException("Set RabbitMqTransportOptions.ServiceName to consume: it names the service's queue.");
        }
        await EnsureConsumingAsync(cancellationToken).ConfigureAwait(false);

        var received = new List<ReceivedMessage>();
        if (!_deliveries.Reader.TryPeek(out _))
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(_receiveWait);
            try
            {
                await _deliveries.Reader.WaitToReadAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return received;
            }
        }
        int generation = Volatile.Read(ref _generation);
        while (received.Count < maxCount && _deliveries.Reader.TryRead(out Delivery? delivery))
        {
            // A delivery from a channel that has since closed is back with the broker already.
            if (delivery.Generation == generation)
            {
                received.Add(delivery);
            }
        }
        return received;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException"><paramref name="message"/> is already settled or was not delivered by this transport.</exception>
    public async ValueTask AckAsync(ReceivedMessage message, CancellationToken cancellationToken)
    {
        Delivery delivery = Settle(message);
        if (IsCurrent(delivery))
        {
            await OnChannelAsync(delivery, channel => channel.BasicAckAsync(delivery.Tag, false, CancellationToken.None)).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The transport has no native delay, so <paramref name="delay"/> is not waited: the message is
    /// published back to the service's queue at once, with its delivery count one higher.
    /// </remarks>
    /// <exception cref="InvalidOperationException"><paramref name="message"/> is already settled or was not delivered by this transport.</exception>
    public ValueTask NackAsync(ReceivedMessage message, TimeSpan delay, CancellationToken cancellationToken)
        => PutBackAsync(message, delay, 1);

    /// <inheritdoc />
    /// <remarks>
    /// The transport has no native delay, so <paramref name="delay"/> is not waited: the message is
    /// published back to the service's queue at once, with the same delivery count.
    /// </remarks>
    /// <exception cref="InvalidOperationException"><paramref name="message"/> is already settled or was not delivered by this transport.</exception>
    public ValueTask DeferAsync(ReceivedMessage message, TimeSpan delay, CancellationToken cancellationToken)
        => PutBackAsync(message, delay, 0);

    // Publishes the message back to the service's queue, its delivery count raised by countsAs: 1 for a
    // failed attempt, 0 for a delivery put back unhandled.
    private async ValueTask PutBackAsync(ReceivedMessage message, TimeSpan delay, int countsAs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        Delivery delivery = Settle(message);
        if (!IsCurrent(delivery))
        {
            return;
        }
        MessageEnvelope envelope = delivery.Envelope;
        int count = delivery.DeliveryCount + countsAs;
        BasicProperties properties = RabbitMqWire.ToProperties(envelope, count, deliveryStamp: _stamp.Sign(envelope.Name, envelope.Id, count));
        try
        {
            await PublishCopyAsync(string.Empty, _options.ServiceName!, properties, envelope.Body).ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (IsCurrent(delivery) && (countsAs > 0 || !HandBack(delivery)))
            {
                // Handed back to the broker. It marks the redelivery, which counts as one more attempt:
                // right for a failed attempt, and for a deferral only when the transport is closing.
                await OnChannelAsync(delivery, channel => channel.BasicNackAsync(delivery.Tag, false, true, CancellationToken.None)).ConfigureAwait(false);
            }
            throw;
        }
        await OnChannelAsync(delivery, channel => channel.BasicAckAsync(delivery.Tag, false, CancellationToken.None)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException"><paramref name="message"/> is already settled or was not delivered by this transport.</exception>
    public async ValueTask DeadLetterAsync(ReceivedMessage message, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reason);
        Delivery delivery = Settle(message);
        if (!IsCurrent(delivery))
        {
            return;
        }
        try
        {
            BasicProperties properties = RabbitMqWire.ToProperties(delivery.Envelope, delivery.DeliveryCount, reason);
            await PublishCopyAsync(_options.DeadLetterExchange, _options.ServiceName!, properties, delivery.Envelope.Body).ConfigureAwait(false);
        }
        catch (Exception) when (IsCurrent(delivery))
        {
            // The queue's own dead-letter exchange keeps the message, without the reason; the consumer
            // has logged the reason already.
            await OnChannelAsync(delivery, channel => channel.BasicNackAsync(delivery.Tag, false, false, CancellationToken.None)).ConfigureAwait(false);
            return;
        }
        await OnChannelAsync(delivery, channel => channel.BasicAckAsync(delivery.Tag, false, CancellationToken.None)).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes the channels and the connection, blocking until they are closed. For a service provider
    /// disposed synchronously; prefer <see cref="DisposeAsync"/>.
    /// </summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>Closes the channels and the connection. Unsettled deliveries go back to the broker.</summary>
    public async ValueTask DisposeAsync()
    {
        await _connectGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _deliveries.Writer.TryComplete();
            await CloseChannelAsync(_consumer).ConfigureAwait(false);
            await CloseChannelAsync(_publisher).ConfigureAwait(false);
            if (_connection is not null)
            {
                await CloseQuietlyAsync(_connection.CloseAsync).ConfigureAwait(false);
                _connection.Dispose();
            }
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private static async Task CloseChannelAsync(IChannel? channel)
    {
        if (channel is not null)
        {
            await CloseQuietlyAsync(channel.CloseAsync).ConfigureAwait(false);
            channel.Dispose();
        }
    }

    private static async Task CloseQuietlyAsync(Func<CancellationToken, Task> close)
    {
        try
        {
            await close(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Closing a connection the broker already dropped; nothing is left to release.
        }
    }

    private Delivery Settle(ReceivedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message is not Delivery delivery || delivery.Owner != this || Interlocked.Exchange(ref delivery.Settled, 1) != 0)
        {
            throw new InvalidOperationException("The message is already settled, or was not delivered by this transport.");
        }
        return delivery;
    }

    private bool IsCurrent(Delivery delivery) => delivery.Generation == Volatile.Read(ref _generation);

    // Gives a deferred delivery whose copy could not be published back to this consumer, still unsettled
    // with the broker, so it comes back with the count it had: a requeue would be marked redelivered and
    // cost it an attempt. False once the transport is closing.
    private bool HandBack(Delivery delivery)
        => _deliveries.Writer.TryWrite(new Delivery(delivery.Envelope, delivery.DeliveryCount, this, delivery.Handle));

    // Settled on the channel the delivery came on: its tag means nothing on any other channel.
    private async Task OnChannelAsync(Delivery delivery, Func<IChannel, ValueTask> settle)
    {
        await _settleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await settle(delivery.Channel).ConfigureAwait(false);
        }
        finally
        {
            _settleGate.Release();
        }
    }

    // Mandatory: a copy the broker cannot route (its queue was deleted) fails rather than vanishes.
    private async Task PublishCopyAsync(string exchange, string routingKey, BasicProperties properties, ReadOnlyMemory<byte> body)
    {
        IChannel publisher = await PublisherAsync(CancellationToken.None).ConfigureAwait(false);
        await _publishGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await publisher.BasicPublishAsync(exchange, routingKey, true, properties, body, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _publishGate.Release();
        }
    }

    private async Task<IChannel> PublisherAsync(CancellationToken cancellationToken)
    {
        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IConnection connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
            if (_publisher is null || ChannelLost(_publisher, connection))
            {
                _publisher?.Dispose();
                _publisher = null;
                _publisher = await connection.CreateChannelAsync(new CreateChannelOptions(true, true), cancellationToken).ConfigureAwait(false);
            }
            return _publisher;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task EnsureConsumingAsync(CancellationToken cancellationToken)
    {
        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IConnection connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
            if (_consumer is not null && !ChannelLost(_consumer, connection))
            {
                return;
            }
            if (_consumer is not null)
            {
                _consumer.Dispose();
                _consumer = null;
            }
            IChannel channel = await connection.CreateChannelAsync(null, cancellationToken).ConfigureAwait(false);
            try
            {
                await channel.BasicQosAsync(0, (ushort)_options.PrefetchCount, false, cancellationToken).ConfigureAwait(false);
                channel.ChannelShutdownAsync += OnChannelShutdownAsync;
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += (_, args) => OnReceivedAsync(channel, args);
                await channel.BasicConsumeAsync(_options.ServiceName!, false, consumer, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                channel.Dispose();
                throw;
            }
            _consumer = channel;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    // A channel the broker closed on its own (a channel error) while the connection stays open is not
    // recovered by the client, so it is replaced. One closed with its connection is left to recovery.
    private static bool ChannelLost(IChannel channel, IConnection connection) => !channel.IsOpen && connection.IsOpen;

    private Task OnChannelShutdownAsync(object sender, ShutdownEventArgs args)
    {
        Interlocked.Increment(ref _generation);
        return Task.CompletedTask;
    }

    private async Task OnReceivedAsync(IChannel channel, BasicDeliverEventArgs args)
    {
        if (args.Body.Length > MaxMessageBytes)
        {
            // Never copied or handed to the consumer; the broker moves it to the dead-letter queue.
            await channel.BasicNackAsync(args.DeliveryTag, false, false, CancellationToken.None).ConfigureAwait(false);
            return;
        }
        MessageEnvelope envelope = RabbitMqWire.ToEnvelope(_options, args, _knownNames);
        var handle = new DeliveryHandle(channel, args.DeliveryTag, Volatile.Read(ref _generation));
        _deliveries.Writer.TryWrite(new Delivery(envelope, _stamp.CountOf(args, envelope), this, handle));
    }

    // Called under the connect gate.
    private async Task<IConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connection is not null)
        {
            return _connection;
        }
        var factory = new ConnectionFactory
        {
            Uri = _options.ConnectionUri,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            ClientProvidedName = _options.ServiceName ?? "rogue-publisher",
        };
        _options.ConfigureConnection?.Invoke(factory);
        IConnection connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DeclareTopologyAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await CloseQuietlyAsync(connection.CloseAsync).ConfigureAwait(false);
            connection.Dispose();
            throw;
        }
        _connection = connection;
        return connection;
    }

    // Declared on a channel of the connection, so the client declares it all again after a recovery.
    private async Task DeclareTopologyAsync(IConnection connection, CancellationToken cancellationToken)
    {
        IChannel channel = await connection.CreateChannelAsync(null, cancellationToken).ConfigureAwait(false);
        await using (channel.ConfigureAwait(false))
        {
            await channel.ExchangeDeclareAsync(_options.Exchange, ExchangeType.Topic, true, false, null, false, cancellationToken).ConfigureAwait(false);
            await channel.ExchangeDeclareAsync(_options.DeadLetterExchange, ExchangeType.Direct, true, false, null, false, cancellationToken).ConfigureAwait(false);
            if (_options.ServiceName is not { } queue)
            {
                return;
            }
            var quorum = new Dictionary<string, object?> { [QueueTypeArgument] = "quorum" };
            await channel.QueueDeclareAsync(_options.DeadLetterQueue!, true, false, false, quorum, false, cancellationToken).ConfigureAwait(false);
            await channel.QueueBindAsync(_options.DeadLetterQueue!, _options.DeadLetterExchange, queue, null, false, cancellationToken).ConfigureAwait(false);

            var arguments = new Dictionary<string, object?>
            {
                [QueueTypeArgument] = "quorum",
                [DeadLetterExchangeArgument] = _options.DeadLetterExchange,
                [DeadLetterRoutingKeyArgument] = queue,
            };
            await channel.QueueDeclareAsync(queue, true, false, false, arguments, false, cancellationToken).ConfigureAwait(false);
            foreach (string name in _subscriptions)
            {
                await channel.QueueBindAsync(queue, _options.Exchange, name, null, false, cancellationToken).ConfigureAwait(false);
            }
            foreach (RabbitMqTransportOptions.RoutingKeyMap map in _options.RoutingKeyMaps)
            {
                await channel.QueueBindAsync(queue, map.Exchange, map.RoutingKey, null, false, cancellationToken).ConfigureAwait(false);
            }
            foreach (RabbitMqTransportOptions.Binding binding in _options.Bindings)
            {
                await channel.QueueBindAsync(queue, binding.Exchange, binding.BindingKey, null, false, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
