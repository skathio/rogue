using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SkathIO.Rogue.Validation;

namespace SkathIO.Rogue.Consumer.App;

/// <summary>Places an order; validated by the code the generator writes for [Validate].</summary>
[Validate]
public sealed partial record PlaceOrder : ICommand<int>
{
    [NotEmpty, MaximumLength(20)]
    public string Customer { get; init; } = string.Empty;

    [GreaterThan(0)]
    public int Quantity { get; init; }
}

/// <summary>A command with no response.</summary>
public sealed record CancelOrders : ICommand;

/// <summary>A query.</summary>
public sealed record CountOrders : IQuery<int>;

/// <summary>An event with two handlers.</summary>
public sealed record OrderPlaced(int OrderNumber) : IEvent;

/// <summary>A stream of the numbers from one to <see cref="Count"/>.</summary>
public sealed record CountTo(int Count) : IStreamQuery<int>;

public sealed class PlaceOrderHandler(OrderBook book, IPublisher publisher, ActivityLog log) : ICommandHandler<PlaceOrder, int>
{
    public async ValueTask<int> Handle(PlaceOrder command, CancellationToken cancellationToken)
    {
        int number = book.Add(command.Customer);
        log.Record("handled PlaceOrder " + number);
        await publisher.Publish(new OrderPlaced(number), cancellationToken).ConfigureAwait(false);
        return number;
    }
}

public sealed class CancelOrdersHandler(OrderBook book, ActivityLog log) : ICommandHandler<CancelOrders>
{
    public ValueTask Handle(CancelOrders command, CancellationToken cancellationToken)
    {
        book.Clear();
        log.Record("handled CancelOrders");
        return default;
    }
}

public sealed class CountOrdersHandler(OrderBook book) : IQueryHandler<CountOrders, int>
{
    public ValueTask<int> Handle(CountOrders query, CancellationToken cancellationToken) => new ValueTask<int>(book.Count);
}

public sealed class SendReceipt(ActivityLog log) : IEventHandler<OrderPlaced>
{
    public ValueTask Handle(OrderPlaced @event, CancellationToken cancellationToken)
    {
        log.Record("receipt for " + @event.OrderNumber);
        return default;
    }
}

public sealed class ReserveStock(ActivityLog log) : IEventHandler<OrderPlaced>
{
    public ValueTask Handle(OrderPlaced @event, CancellationToken cancellationToken)
    {
        log.Record("stock for " + @event.OrderNumber);
        return default;
    }
}

public sealed class CountToHandler : IStreamQueryHandler<CountTo, int>
{
    public async IAsyncEnumerable<int> Handle(CountTo query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (int i = 1; i <= query.Count; i++)
        {
            await Task.Yield();
            yield return i;
        }
    }
}

/// <summary>The orders placed so far.</summary>
public sealed class OrderBook
{
    private readonly List<string> _customers = new List<string>();
    private readonly object _gate = new object();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _customers.Count;
            }
        }
    }

    public int Add(string customer)
    {
        lock (_gate)
        {
            _customers.Add(customer);
            return _customers.Count;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _customers.Clear();
        }
    }
}
