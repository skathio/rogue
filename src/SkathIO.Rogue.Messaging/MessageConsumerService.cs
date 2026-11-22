using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace SkathIO.Rogue.Messaging;

/// <summary>
/// Runs the <see cref="MessageConsumer"/> for the life of the host: <c>AddRogueMessageConsumer</c>
/// registers it, so a generic or web host that asks for the consumer starts it and stops it on shutdown.
/// </summary>
internal sealed class MessageConsumerService : BackgroundService
{
    private readonly MessageConsumer _consumer;

    public MessageConsumerService(MessageConsumer consumer) => _consumer = consumer;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _consumer.RunAsync(stoppingToken);
}
