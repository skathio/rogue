using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RabbitMQ.Client;

namespace SkathIO.Rogue.Messaging.RabbitMQ;

/// <summary>The exchanges, queues and bindings the transport declares when it connects.</summary>
public sealed partial class RabbitMqTransport
{
    private const string QueueTypeArgument = "x-queue-type";
    private const string DeadLetterExchangeArgument = "x-dead-letter-exchange";
    private const string DeadLetterRoutingKeyArgument = "x-dead-letter-routing-key";
    private const string DeadLetterStrategyArgument = "x-dead-letter-strategy";
    private const string OverflowArgument = "x-overflow";

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

            // No consumer reads the wait queue: each copy expires there and goes back to the service's
            // queue through the default exchange, where its signed count is read. At-least-once
            // dead-lettering keeps a copy until the service's queue has taken it, which quorum queues
            // allow only with reject-publish overflow.
            var wait = new Dictionary<string, object?>
            {
                [QueueTypeArgument] = "quorum",
                [DeadLetterExchangeArgument] = string.Empty,
                [DeadLetterRoutingKeyArgument] = queue,
                [DeadLetterStrategyArgument] = "at-least-once",
                [OverflowArgument] = "reject-publish",
            };
            await channel.QueueDeclareAsync(_options.WaitQueue!, true, false, false, wait, false, cancellationToken).ConfigureAwait(false);
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
