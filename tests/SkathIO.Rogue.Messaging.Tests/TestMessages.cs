using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SkathIO.Rogue.Messaging;

namespace SkathIO.Rogue.Messaging.Tests;

/// <summary>What the test handlers saw. One per service provider.</summary>
public sealed class MessageProbe
{
    public ConcurrentQueue<string> Ran { get; } = new ConcurrentQueue<string>();

    /// <summary>Set once a handler told to wait has started.</summary>
    public TaskCompletionSource Started { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Set once <see cref="RunTarget"/> handlers have run.</summary>
    public TaskCompletionSource ReachedTarget { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public int RunTarget { get; set; } = int.MaxValue;

    public void Record(string run)
    {
        Ran.Enqueue(run);
        if (Ran.Count >= RunTarget)
        {
            ReachedTarget.TrySetResult();
        }
    }
}

/// <summary>
/// Its handler records it, and by its note: "fail" throws, "wait" waits until cancelled, and
/// "follow-up" publishes an <see cref="OrderAudited"/> through the publisher.
/// </summary>
[Message("test.order-placed")]
public sealed record OrderPlaced(int Id, string Note) : IEvent;

/// <summary>An event with no handler, published from <see cref="OrderPlaced"/>'s handler.</summary>
[Message("test.order-audited")]
public sealed record OrderAudited(int Id) : IEvent;

[Message("test.reserve")]
public sealed record ReserveStock(int Id) : ICommand<int>;

/// <summary>Neither an event nor a command: it can be sent, but not handled here.</summary>
[Message("test.report")]
public sealed record StockReport(int Id);

/// <summary>An event whose body can only be read by a converter that always fails.</summary>
[Message("test.strict")]
public sealed record StrictValue([property: JsonConverter(typeof(FailingConverter))] int Value) : IEvent;

/// <summary>Throws on every read, as a custom converter rejecting its input might.</summary>
public sealed class FailingConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new FormatException("rejected");

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}

public sealed class OrderPlacedHandler : IEventHandler<OrderPlaced>
{
    private readonly MessageProbe _probe;
    private readonly IPublisher _publisher;

    public OrderPlacedHandler(MessageProbe probe, IPublisher publisher)
    {
        _probe = probe;
        _publisher = publisher;
    }

    public async ValueTask Handle(OrderPlaced e, CancellationToken cancellationToken)
    {
        _probe.Record("placed:" + e.Id);
        switch (e.Note)
        {
            case "fail":
                throw new InvalidOperationException("handler failed");
            case "wait":
                _probe.Started.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                break;
            case "follow-up":
                await _publisher.Publish(new OrderAudited(e.Id), cancellationToken);
                break;
        }
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
[JsonSerializable(typeof(OrderAudited))]
[JsonSerializable(typeof(ReserveStock))]
[JsonSerializable(typeof(StockReport))]
[JsonSerializable(typeof(StrictValue))]
internal sealed partial class TestMessagesJson : JsonSerializerContext
{
}
