using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SkathIO.Rogue.Messaging;

namespace SkathIO.Rogue.Consumer.Messaging.Tests;

/// <summary>
/// The event the orders service publishes and both services handle. Its handler fails when the note
/// is <see cref="Fail"/>, and holds until the probe lets it go, or its token is cancelled, when the
/// note is <see cref="Hold"/>.
/// </summary>
[Message("shop.order-placed")]
public sealed record OrderPlaced(int OrderId, string Note) : IEvent
{
    public const string Fail = "fail";
    public const string Hold = "hold";
}

/// <summary>What one service instance's handlers did. One per service provider.</summary>
public sealed class ShopProbe
{
    private readonly ConcurrentQueue<int> _started = new ConcurrentQueue<int>();
    private readonly ConcurrentQueue<int> _completed = new ConcurrentQueue<int>();
    private readonly ConcurrentQueue<int> _cancelled = new ConcurrentQueue<int>();
    private readonly TaskCompletionSource _firstStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _letGo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The orders whose handler started, in order.</summary>
    public int[] Started => _started.ToArray();

    /// <summary>The orders whose handler finished without failing, in order.</summary>
    public int[] Completed => _completed.ToArray();

    /// <summary>The orders whose held handler was cancelled, in order.</summary>
    public int[] Cancelled => _cancelled.ToArray();

    /// <summary>Completes when a handler first starts.</summary>
    public Task FirstStart => _firstStart.Task;

    /// <summary>Lets every held handler, now and later, finish.</summary>
    public void LetGo() => _letGo.TrySetResult();

    public int Count(int orderId) => _started.Count(id => id == orderId);

    internal async ValueTask HandleAsync(OrderPlaced e, CancellationToken cancellationToken)
    {
        _started.Enqueue(e.OrderId);
        _firstStart.TrySetResult();
        if (e.Note == OrderPlaced.Fail)
        {
            throw new InvalidOperationException("Order " + e.OrderId + " always fails.");
        }
        if (e.Note == OrderPlaced.Hold)
        {
            try
            {
                await _letGo.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _cancelled.Enqueue(e.OrderId);
                throw;
            }
        }
        _completed.Enqueue(e.OrderId);
    }
}

public sealed class OrderPlacedHandler : IEventHandler<OrderPlaced>
{
    private readonly ShopProbe _probe;

    public OrderPlacedHandler(ShopProbe probe) => _probe = probe;

    public ValueTask Handle(OrderPlaced e, CancellationToken cancellationToken) => _probe.HandleAsync(e, cancellationToken);
}

/// <summary>Serializer metadata for the messages, so nothing is reflected at run time.</summary>
[JsonSerializable(typeof(OrderPlaced))]
internal sealed partial class ShopJson : JsonSerializerContext
{
}
