using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue.Messaging.RabbitMQ.Tests;

/// <summary>What the test handlers saw. One per service provider.</summary>
public sealed class MessageProbe
{
    private readonly ConcurrentQueue<string> _ran = new ConcurrentQueue<string>();

    public string[] Ran => _ran.ToArray();

    public int Count(string run) => _ran.Count(r => r == run);

    public void Record(string run) => _ran.Enqueue(run);
}

/// <summary>Its handler records it, and throws when its note is "fail".</summary>
[Message("rmq.order-placed")]
public sealed record OrderPlaced(int Id, string Note) : IEvent;

[Message("rmq.reserve")]
public sealed record ReserveStock(int Id) : ICommand<int>;

/// <summary>Neither an event nor a command: it is only sent, so the service's queue is not bound to it.</summary>
[Message("rmq.report")]
public sealed record StockReport(int Id);

public sealed class OrderPlacedHandler : IEventHandler<OrderPlaced>
{
    private readonly MessageProbe _probe;

    public OrderPlacedHandler(MessageProbe probe) => _probe = probe;

    public ValueTask Handle(OrderPlaced e, CancellationToken cancellationToken)
    {
        _probe.Record("placed:" + e.Id);
        if (e.Note == "fail")
        {
            throw new InvalidOperationException("handler failed");
        }
        return default;
    }
}

public sealed class ReserveStockHandler : ICommandHandler<ReserveStock, int>
{
    private readonly MessageProbe _probe;

    public ReserveStockHandler(MessageProbe probe) => _probe = probe;

    public ValueTask<int> Handle(ReserveStock command, CancellationToken cancellationToken)
    {
        _probe.Record("reserve:" + command.Id);
        return new ValueTask<int>(command.Id);
    }
}

[JsonSerializable(typeof(OrderPlaced))]
[JsonSerializable(typeof(ReserveStock))]
[JsonSerializable(typeof(StockReport))]
internal sealed partial class TestMessagesJson : JsonSerializerContext
{
}
