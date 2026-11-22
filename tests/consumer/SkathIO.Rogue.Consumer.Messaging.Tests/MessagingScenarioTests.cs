using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RabbitMQ.Client;
using SkathIO.Rogue.Messaging;
using Xunit;

namespace SkathIO.Rogue.Consumer.Messaging.Tests;

/// <summary>
/// Two services, orders and billing, message each other over RabbitMQ from the packed packages: the
/// outbox relays what a database transaction commits, the inbox runs a message delivered twice once,
/// a service drops its own events, every instance reads the delivery counts the others signed, a
/// delivery another instance is handling waits in the wait queue, and a stopped service puts its
/// message back. Each test runs in a virtual host of its own. The outcomes do not depend on timing:
/// a test waits for a state, and the time limit only ends a test that hangs.
/// </summary>
public sealed class MessagingScenarioTests : IClassFixture<ShopBroker>
{
    private const string Orders = "orders";
    private const string Billing = "billing";

    // The header the transport keeps a dead-lettered message's delivery count in.
    private const string DeliveryCountHeader = "rogue-delivery-count";

    // Guards against a hang only; no assertion depends on how long anything takes.
    private static readonly TimeSpan _hang = TimeSpan.FromMinutes(2);

    private readonly ShopBroker _broker;

    public MessagingScenarioTests(ShopBroker broker) => _broker = broker;

    [RabbitMqFact]
    public async Task Outbox_RelaysWhatATransactionCommits_ToTheOtherService_AndNothingOfARolledBackOne()
    {
        await using Shop shop = await _broker.NewShopAsync();
        await using MessagingService orders = MessagingService.StartService(shop, Orders);
        await using MessagingService billing = MessagingService.StartService(shop, Billing);
        await DeclareQueueAsync(billing);

        await using (NpgsqlConnection connection = await shop.DataSource.OpenConnectionAsync())
        {
            await using (NpgsqlTransaction rolledBack = await connection.BeginTransactionAsync())
            {
                await orders.Outbox.Publish(new OrderPlaced(1, "rolled back"), rolledBack);
                await rolledBack.RollbackAsync();
            }
            await using NpgsqlTransaction committed = await connection.BeginTransactionAsync();
            await orders.Outbox.Publish(new OrderPlaced(2, "committed"), committed);
            await committed.CommitAsync();
        }
        int relayed = await orders.Worker.RunOnceAsync();
        await RunUntilAsync(() => billing.Probe.Completed.Length >= 1, () => billing.Consumer.RunOnceAsync());

        Assert.Equal(1, relayed);
        Assert.Equal(new[] { 2 }, billing.Probe.Completed);
        Assert.Equal(0, await orders.Worker.RunOnceAsync());
        Assert.Equal(0u, await shop.MessagesInAsync(Billing));
    }

    [RabbitMqFact]
    public async Task Inbox_RunsTheHandlersOnce_WhenTheSameMessageIdIsDeliveredTwice()
    {
        await using Shop shop = await _broker.NewShopAsync();
        await using MessagingService orders = MessagingService.StartService(shop, Orders);
        await using MessagingService billing = MessagingService.StartService(shop, Billing);
        await DeclareQueueAsync(billing);

        // Sent twice with one id, as a relay that ran again after its send sends it.
        MessageEnvelope envelope = orders.Bus.CreateEnvelope(new OrderPlaced(3, "relayed twice"));
        await orders.Wire.SendAsync(new[] { envelope, envelope }, CancellationToken.None);
        int received = 0;
        await RunUntilAsync(() => received >= 2, async () => received += await billing.Consumer.RunOnceAsync());

        Assert.Equal(2, received);
        Assert.Equal(new[] { 3 }, billing.Probe.Started);
        Assert.Equal(new[] { 3 }, billing.Probe.Completed);
        Assert.Equal(0u, await shop.MessagesInAsync(Billing));
        Assert.Equal(0u, await shop.MessagesInAsync(billing.Transport.WaitQueue!));
        Assert.Equal(0u, await shop.MessagesInAsync(billing.Transport.DeadLetterQueue!));
    }

    [RabbitMqFact]
    public async Task AnEventPublishedThroughIPublisher_RunsItsLocalHandlersOnce_ItsOwnOriginCopyIsDropped_AndTheOtherServiceHandlesIt()
    {
        await using Shop shop = await _broker.NewShopAsync();
        await using MessagingService orders = MessagingService.StartService(shop, Orders);
        await using MessagingService billing = MessagingService.StartService(shop, Billing);
        await DeclareQueueAsync(orders);
        await DeclareQueueAsync(billing);

        await using (AsyncServiceScope scope = orders.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(new OrderPlaced(5, "published"));
        }
        int[] ranOnPublish = orders.Probe.Completed;
        int ownCopies = 0;
        await RunUntilAsync(
            () => ownCopies >= 1 && billing.Probe.Completed.Length >= 1,
            async () =>
            {
                ownCopies += await orders.Consumer.RunOnceAsync();
                await billing.Consumer.RunOnceAsync();
            });

        Assert.Equal(new[] { 5 }, ranOnPublish);
        Assert.Equal(1, ownCopies);
        Assert.Equal(new[] { 5 }, orders.Probe.Started);
        Assert.Equal(new[] { 5 }, billing.Probe.Completed);
        Assert.Equal(0u, await shop.MessagesInAsync(Orders));
        Assert.Equal(0u, await shop.MessagesInAsync(orders.Transport.DeadLetterQueue!));
    }

    [RabbitMqFact]
    public async Task AMessageThatAlwaysFails_IsDeadLetteredAfterMaxAttempts_HoweverItsDeliveriesAreSpreadOverTheInstances()
    {
        await using Shop shop = await _broker.NewShopAsync();
        await using MessagingService orders = MessagingService.StartService(shop, Orders);
        // Two instances of billing on one queue, each holding one delivery at a time. Under the shared
        // stamp key each reads the count the other signed on the copy it put back.
        await using MessagingService first = MessagingService.StartService(shop, Billing, prefetchCount: 1);
        await using MessagingService second = MessagingService.StartService(shop, Billing, prefetchCount: 1);
        await DeclareQueueAsync(first);

        await orders.Bus.Publish(new OrderPlaced(9, OrderPlaced.Fail));
        BasicGetResult? deadLettered = null;
        await RunUntilAsync(
            () => deadLettered is not null,
            async () =>
            {
                await first.Consumer.RunOnceAsync();
                await second.Consumer.RunOnceAsync();
                deadLettered ??= await shop.TakeAsync(first.Transport.DeadLetterQueue!);
            });

        Assert.Equal(3, first.Probe.Count(9) + second.Probe.Count(9));
        Assert.Equal(3, Assert.IsType<int>(deadLettered!.BasicProperties.Headers![DeliveryCountHeader]));
        Assert.Empty(first.Probe.Completed);
        Assert.Empty(second.Probe.Completed);
        Assert.Equal(0u, await shop.MessagesInAsync(Billing));
    }

    [RabbitMqFact]
    public async Task ADeliveryWhoseIdAnotherInstanceIsHandling_IsDeferred_IntoTheWaitQueue()
    {
        await using Shop shop = await _broker.NewShopAsync();
        await using MessagingService orders = MessagingService.StartService(shop, Orders);
        await using MessagingService first = MessagingService.StartService(shop, Billing, prefetchCount: 1);
        // Long enough that the deferred copy is still in the wait queue when the test reads it.
        await using MessagingService second = MessagingService.StartService(shop, Billing, busyDelay: TimeSpan.FromHours(1));
        await DeclareQueueAsync(first);

        MessageEnvelope envelope = orders.Bus.CreateEnvelope(new OrderPlaced(11, OrderPlaced.Hold));
        Task<int> holding = await HoldFirstDeliveryAsync(first, orders, envelope);
        int deferred = await DeferDuplicateAsync(second, orders, envelope);
        uint waiting = await shop.MessagesInAsync(second.Transport.WaitQueue!);
        uint ready = await shop.MessagesInAsync(Billing);
        first.Probe.LetGo();
        int handled = await holding;

        Assert.Equal(1, deferred);
        Assert.Equal(1u, waiting);
        Assert.Equal(0u, ready);
        Assert.Equal(1, handled);
        Assert.Equal(new[] { 11 }, first.Probe.Completed);
        Assert.Empty(second.Probe.Started);
    }

    [RabbitMqFact]
    public async Task ADeferredDelivery_ComesBackFromTheWaitQueue_AndIsDroppedOnceItsIdIsHandled()
    {
        await using Shop shop = await _broker.NewShopAsync();
        await using MessagingService orders = MessagingService.StartService(shop, Orders);
        await using MessagingService first = MessagingService.StartService(shop, Billing, prefetchCount: 1);
        await using MessagingService second = MessagingService.StartService(shop, Billing);
        await DeclareQueueAsync(first);

        MessageEnvelope envelope = orders.Bus.CreateEnvelope(new OrderPlaced(13, OrderPlaced.Hold));
        Task<int> holding = await HoldFirstDeliveryAsync(first, orders, envelope);
        int deferred = await DeferDuplicateAsync(second, orders, envelope);
        first.Probe.LetGo();
        int handled = await holding;
        int returned = 0;
        await RunUntilAsync(
            () => returned >= 1,
            async () =>
            {
                returned += await first.Consumer.RunOnceAsync();
                returned += await second.Consumer.RunOnceAsync();
            });

        Assert.Equal((1, 1, 1), (deferred, handled, returned));
        Assert.Equal(new[] { 13 }, first.Probe.Started);
        Assert.Empty(second.Probe.Started);
        Assert.Equal(0u, await shop.MessagesInAsync(Billing));
        Assert.Equal(0u, await shop.MessagesInAsync(second.Transport.WaitQueue!));
        Assert.Equal(0u, await shop.MessagesInAsync(second.Transport.DeadLetterQueue!));
    }

    [RabbitMqFact]
    public async Task OnShutdown_TheRunningHandlerIsCancelled_AndItsMessageIsPutBack_ForAnotherInstance()
    {
        await using Shop shop = await _broker.NewShopAsync();
        await using MessagingService orders = MessagingService.StartService(shop, Orders);
        await using MessagingService stopping = MessagingService.StartService(shop, Billing, prefetchCount: 1);
        await using MessagingService successor = MessagingService.StartService(shop, Billing);
        using var guard = new CancellationTokenSource(_hang);
        await DeclareQueueAsync(stopping);

        await orders.Bus.Publish(new OrderPlaced(7, OrderPlaced.Hold));
        await stopping.StartAsync(guard.Token);
        await stopping.Probe.FirstStart.WaitAsync(guard.Token);
        await stopping.StopAsync(guard.Token);
        ShopProbe stopped = stopping.Probe;
        // Closes its connection, so the broker no longer sends it anything.
        await stopping.DisposeAsync();
        successor.Probe.LetGo();
        await RunUntilAsync(() => successor.Probe.Completed.Length >= 1, () => successor.Consumer.RunOnceAsync());

        Assert.Equal(new[] { 7 }, stopped.Started);
        Assert.Equal(new[] { 7 }, stopped.Cancelled);
        Assert.Empty(stopped.Completed);
        Assert.Equal(new[] { 7 }, successor.Probe.Completed);
        Assert.Equal(0u, await shop.MessagesInAsync(Billing));
        Assert.Equal(0u, await shop.MessagesInAsync(successor.Transport.DeadLetterQueue!));
    }

    // A service's queue is declared, and bound to the event, when its consumer first receives; anything
    // published before then reaches no queue. Nothing is published yet, so this receives nothing.
    private static async Task DeclareQueueAsync(MessagingService service) => Assert.Equal(0, await service.Consumer.RunOnceAsync());

    // Sends the message to the first instance, the only one consuming, and returns its consumer's run
    // once the handler holds the message; its one-delivery window then keeps the next delivery from it.
    private static async Task<Task<int>> HoldFirstDeliveryAsync(MessagingService first, MessagingService orders, MessageEnvelope envelope)
    {
        using var guard = new CancellationTokenSource(_hang);
        await orders.Wire.SendAsync(new[] { envelope }, CancellationToken.None);
        while (true)
        {
            Task<int> run = first.Consumer.RunOnceAsync();
            Task ready = await Task.WhenAny(first.Probe.FirstStart, run).WaitAsync(guard.Token);
            if (ReferenceEquals(ready, first.Probe.FirstStart))
            {
                return run;
            }
            await run;
        }
    }

    // Sends the same message id again, as a second relay would, and has the second instance receive it:
    // the inbox reports the id busy, so the delivery is deferred. Returns how many it received.
    private static async Task<int> DeferDuplicateAsync(MessagingService second, MessagingService orders, MessageEnvelope envelope)
    {
        await orders.Wire.SendAsync(new[] { envelope }, CancellationToken.None);
        int received = 0;
        await RunUntilAsync(() => received >= 1, async () => received += await second.Consumer.RunOnceAsync());
        return received;
    }

    // Runs rounds until the state is reached. A receive waits up to a second for a delivery, so the
    // rounds do not spin.
    private static async Task RunUntilAsync(Func<bool> done, Func<Task> round)
    {
        using var guard = new CancellationTokenSource(_hang);
        while (!done())
        {
            Assert.False(guard.IsCancellationRequested, "Gave up waiting for the services to reach the state the test waits for.");
            await round();
        }
    }
}
