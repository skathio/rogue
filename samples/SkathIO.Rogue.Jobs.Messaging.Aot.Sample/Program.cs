// Jobs and messaging under native AOT. A [Job] command is enqueued and run by the worker; its handler
// publishes a [Message] event on the bus, and the consumer delivers it to the event's handler. CI
// publishes this with PublishAot=true, fails on any trim or AOT warning, and runs the binary.
using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue;
using SkathIO.Rogue.Jobs;
using SkathIO.Rogue.Messaging;
using SkathIO.Rogue.Samples.JobsMessaging;

var services = new ServiceCollection();
services.AddSingleton<ShippedOrders>();
services.AddSingleton<IMessageTransport>(new InMemoryTransport());
services.AddRogue();
services.AddRogueJobs(static options => options.SerializerContext = SampleJson.Default);
services.AddRogueMessaging(static options => options.SerializerContext = SampleJson.Default);
await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

await provider.GetRequiredService<IJobScheduler>().Enqueue(new ShipOrder(42));
int jobsRun = await provider.GetRequiredService<JobWorker>().RunOnceAsync();
int messagesHandled = await provider.GetRequiredService<MessageConsumer>().RunOnceAsync();

ShippedOrders shipped = provider.GetRequiredService<ShippedOrders>();
System.Console.WriteLine("jobs " + jobsRun + ", messages " + messagesHandled + ", shipped " + string.Join(",", shipped.Ids));

return jobsRun == 1 && messagesHandled == 1 && shipped.Ids.Count == 1 && shipped.Ids.TryPeek(out int id) && id == 42 ? 0 : 1;

namespace SkathIO.Rogue.Samples.JobsMessaging
{
    /// <summary>A background job: ship an order, then tell other services it shipped.</summary>
    [Job(MaxAttempts = 3)]
    public sealed record ShipOrder(int OrderId) : ICommand;

    /// <summary>An event other services consume, under a stable name rather than its CLR type name.</summary>
    [Message("sample.order-shipped")]
    public sealed record OrderShipped(int OrderId) : IEvent;

    /// <summary>The order ids the event handler saw.</summary>
    public sealed class ShippedOrders
    {
        public ConcurrentQueue<int> Ids { get; } = new ConcurrentQueue<int>();
    }

    /// <summary>Runs as a job, and publishes <see cref="OrderShipped"/> on the bus.</summary>
    public sealed class ShipOrderHandler : ICommandHandler<ShipOrder>
    {
        private readonly IMessageBus _bus;

        public ShipOrderHandler(IMessageBus bus) => _bus = bus;

        public ValueTask Handle(ShipOrder command, CancellationToken cancellationToken)
            => _bus.Publish(new OrderShipped(command.OrderId), cancellationToken);
    }

    /// <summary>Handles the event when the consumer receives it from the transport.</summary>
    public sealed class OrderShippedHandler : IEventHandler<OrderShipped>
    {
        private readonly ShippedOrders _shipped;

        public OrderShippedHandler(ShippedOrders shipped) => _shipped = shipped;

        public ValueTask Handle(OrderShipped e, CancellationToken cancellationToken)
        {
            _shipped.Ids.Enqueue(e.OrderId);
            return default;
        }
    }

    /// <summary>Serializer metadata for every job command and message, so nothing is reflected at run time.</summary>
    [JsonSerializable(typeof(ShipOrder))]
    [JsonSerializable(typeof(OrderShipped))]
    internal sealed partial class SampleJson : JsonSerializerContext
    {
    }
}
