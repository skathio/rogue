using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue.Jobs;
using SkathIO.Rogue.Jobs.StoreContract;
using Xunit;

namespace SkathIO.Rogue.Messaging.Outbox.Tests;

/// <summary>
/// The outbox and inbox over an in-memory store. The database stores run the same flow in their own
/// container tests; these show the package's own logic without a database.
/// </summary>
public sealed class MessageOutboxTests
{
    [Fact]
    public async Task Publish_InATransaction_IsRelayedByAJob_OnceItCommits()
    {
        await using OutboxHost host = OutboxHost.Create();
        using var transaction = new FakeTransaction();

        string id = await host.Outbox.Publish(new OrderPlaced(7), transaction);
        int ranBeforeCommit = await host.Worker.RunOnceAsync();
        transaction.Commit();
        int ranAfterCommit = await host.Worker.RunOnceAsync();

        Assert.Equal(0, ranBeforeCommit);
        Assert.Equal(1, ranAfterCommit);
        MessageEnvelope sent = Assert.Single(await host.DrainAsync());
        Assert.Equal(id, sent.Id);
        Assert.Equal("outbox.order-placed", sent.Name);
        Assert.Equal(id, sent.CorrelationId);
        Assert.Equal(MessageEnvelope.JsonContentType, sent.ContentType);
        Assert.Equal(new OrderPlaced(7), JsonSerializer.Deserialize(sent.Body.Span, OutboxMessagesJson.Default.OrderPlaced));
    }

    [Fact]
    public async Task Publish_InATransactionThatRollsBack_SendsNothing()
    {
        await using OutboxHost host = OutboxHost.Create();
        using var transaction = new FakeTransaction();

        await host.Outbox.Publish(new OrderPlaced(7), transaction);
        transaction.Rollback();
        int ran = await host.Worker.RunOnceAsync();

        Assert.Equal(0, ran);
        Assert.Empty(await host.DrainAsync());
    }

    [Fact]
    public async Task Publish_OnAStoreThatCannotJoinATransaction_IsRefused()
    {
        await using OutboxHost host = OutboxHost.Create(new InMemoryJobStore());
        using var transaction = new FakeTransaction();

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await host.Outbox.Publish(new OrderPlaced(7), transaction));

        Assert.Contains("cannot join a database transaction", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, await host.Worker.RunOnceAsync());
    }

    [Fact]
    public async Task Publish_RefusesNullArguments_AndATypeThatIsNotAMessage_StoringNothing()
    {
        await using OutboxHost host = OutboxHost.Create();
        using var transaction = new FakeTransaction();

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await host.Outbox.Publish<OrderPlaced>(null!, transaction));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await host.Outbox.Publish(new OrderPlaced(7), (DbTransaction)null!));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await host.Outbox.Publish("not a message", transaction));
        transaction.Commit();

        Assert.Equal(0, await host.Worker.RunOnceAsync());
    }

    [Theory]
    [InlineData(8, false)]
    [InlineData(7, true)]
    public async Task Publish_OverMaxMessageBytes_IsRefused_StoringNothing(int limit, bool refused)
    {
        // {"Id":7} is 8 bytes.
        await using ServiceProvider services = Tight(maxMessageBytes: limit);
        using var transaction = new FakeTransaction();

        Exception? thrown = await Record.ExceptionAsync(async () => await services.GetRequiredService<IMessageOutbox>().Publish(new OrderPlaced(7), transaction));
        transaction.Commit();

        Assert.Equal(refused, thrown is InvalidOperationException);
        Assert.Equal(refused ? 0 : 1, await services.GetRequiredService<JobWorker>().RunOnceAsync());
    }

    [Fact]
    public async Task RelayThatFails_IsRetried_AndSendsTheSameMessageId()
    {
        var transport = new FlakyTransport(failures: 1);
        await using ServiceProvider services = Tight(transport: transport);
        using var transaction = new FakeTransaction();

        string id = await services.GetRequiredService<IMessageOutbox>().Publish(new OrderPlaced(7), transaction);
        transaction.Commit();
        JobWorker worker = services.GetRequiredService<JobWorker>();
        await worker.RunOnceAsync();
        ((ManualTimeProvider)services.GetRequiredService<TimeProvider>()).Advance(TimeSpan.FromHours(2));
        await worker.RunOnceAsync();

        Assert.Equal(new[] { id, id }, transport.Attempts);
        Assert.Equal(id, Assert.Single(transport.Sent).Id);
    }

    [Fact]
    public async Task DuplicateDelivery_OfARelayedMessage_RunsTheHandlerOnce()
    {
        await using OutboxHost host = OutboxHost.Create();
        using var transaction = new FakeTransaction();
        await host.Outbox.Publish(new OrderPlaced(7), transaction);
        transaction.Commit();
        await host.Worker.RunOnceAsync();
        MessageEnvelope relayed = Assert.Single(await host.DrainAsync());

        // The relay ran twice, as it can when its worker stops after sending: the same id arrives twice.
        await host.Transport.SendAsync(new[] { relayed, relayed }, CancellationToken.None);
        await host.Consumer.RunOnceAsync();

        Assert.Equal(new[] { 7 }, host.Handled.Orders);
        Assert.Empty(host.Transport.DeadLettered);
        Assert.Empty(await host.DrainAsync());
    }

    [Fact]
    public async Task MessagesWithDifferentIds_EachRunTheHandler()
    {
        await using OutboxHost host = OutboxHost.Create(new InMemoryJobStore());
        IMessageBus bus = host.Services.GetRequiredService<IMessageBus>();

        await bus.Publish(new OrderPlaced(1));
        await bus.Publish(new OrderPlaced(2));
        await host.Consumer.RunOnceAsync();

        Assert.Equal(new[] { 1, 2 }, host.Handled.Orders.OrderBy(static o => o));
    }

    [Fact]
    public async Task Inbox_OnAStoreThatKeepsNone_FailsWhenResolved()
    {
        await using OutboxHost host = OutboxHost.Create(new StoreWithoutInbox());

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => host.Services.GetRequiredService<IMessageInbox>());

        Assert.Contains("keeps no message inbox", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InboxLease_MustBePositive()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentOutOfRangeException>(() => services.AddRogueMessagingOutbox(static o => o.InboxLease = TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => services.AddRogueMessagingOutbox(static o => o.InboxLease = TimeSpan.FromTicks(-1)));
        services.AddRogueMessagingOutbox(static o => o.InboxLease = TimeSpan.FromTicks(1));
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddRogueMessagingOutbox());
    }

    [Fact]
    public async Task InboxLease_IsHowLongAClaimHoldsTheMessageId()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = new InMemoryJobStore(clock);
        await using OutboxHost host = OutboxHost.Create(store, static o => o.InboxLease = TimeSpan.FromSeconds(30));
        IMessageInbox inbox = host.Services.GetRequiredService<IMessageInbox>();

        InboxClaim first = await inbox.TryClaimAsync("m-1", CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromMilliseconds(1));
        InboxClaim held = await inbox.TryClaimAsync("m-1", CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        InboxClaim ranOut = await inbox.TryClaimAsync("m-1", CancellationToken.None);

        Assert.Equal(InboxClaim.Claimed, first);
        Assert.Equal(InboxClaim.Busy, held);
        Assert.Equal(InboxClaim.Claimed, ranOut);
    }

    [Fact]
    public async Task RegisteringTwice_AddsOneRelayJob_AndKeepsTheFirstOptions()
    {
        var services = new ServiceCollection();
        services.AddRogueMessagingOutbox(static o => o.InboxLease = TimeSpan.FromSeconds(30));
        services.AddRogueMessagingOutbox(static o => o.InboxLease = TimeSpan.FromSeconds(90));
        await using ServiceProvider provider = services.BuildServiceProvider();

        JobDefinition relay = Assert.Single(provider.GetServices<JobDefinition>());
        Assert.Equal("rogue.messaging.outbox", relay.Kind);
        Assert.Equal(TimeSpan.FromSeconds(30), provider.GetRequiredService<MessageOutboxOptions>().InboxLease);
    }

    // A provider like OutboxHost's, with the given message size limit or transport.
    private static ServiceProvider Tight(int maxMessageBytes = 256 * 1024, IMessageTransport? transport = null)
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var collection = new ServiceCollection();
        collection.AddSingleton<TimeProvider>(clock);
        collection.AddSingleton<Handled>();
        collection.AddSingleton(transport ?? new InMemoryTransport(clock));
        collection.AddSingleton<IJobStore>(new TransactionalStore(new InMemoryJobStore(clock)));
        collection.AddRogue();
        collection.AddRogueMessaging(o =>
        {
            o.SerializerContext = OutboxMessagesJson.Default;
            o.MaxMessageBytes = maxMessageBytes;
        });
        collection.AddRogueJobs(static o => o.BackoffJitter = 0);
        collection.AddRogueMessagingOutbox();
        return collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>A transport whose first sends fail, recording the id of every send it was asked for.</summary>
    private sealed class FlakyTransport : IMessageTransport
    {
        private int _failures;

        public FlakyTransport(int failures) => _failures = failures;

        public List<string> Attempts { get; } = new List<string>();

        public List<MessageEnvelope> Sent { get; } = new List<MessageEnvelope>();

        public TransportCapabilities Capabilities => TransportCapabilities.None;

        public ValueTask SendAsync(IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken)
        {
            Attempts.AddRange(messages.Select(static m => m.Id));
            if (_failures-- > 0)
            {
                throw new InvalidOperationException("The broker is down.");
            }
            Sent.AddRange(messages);
            return default;
        }

        public ValueTask<IReadOnlyList<ReceivedMessage>> ReceiveAsync(int maxCount, CancellationToken cancellationToken) => new ValueTask<IReadOnlyList<ReceivedMessage>>(Array.Empty<ReceivedMessage>());

        public ValueTask AckAsync(ReceivedMessage message, CancellationToken cancellationToken) => default;

        public ValueTask NackAsync(ReceivedMessage message, TimeSpan delay, CancellationToken cancellationToken) => default;

        public ValueTask DeferAsync(ReceivedMessage message, TimeSpan delay, CancellationToken cancellationToken) => default;

        public ValueTask DeadLetterAsync(ReceivedMessage message, string reason, CancellationToken cancellationToken) => default;
    }

    /// <summary>A job store that keeps no inbox.</summary>
    private sealed class StoreWithoutInbox : IJobStore
    {
        private readonly InMemoryJobStore _inner = new InMemoryJobStore();

        public ValueTask EnqueueAsync(IReadOnlyList<NewJob> jobs, CancellationToken cancellationToken) => _inner.EnqueueAsync(jobs, cancellationToken);

        public ValueTask<Guid> EnqueueOnceAsync(NewJob job, CancellationToken cancellationToken) => _inner.EnqueueOnceAsync(job, cancellationToken);

        public ValueTask<bool> TryAcquireLeadershipAsync(string name, string owner, TimeSpan lease, CancellationToken cancellationToken) => _inner.TryAcquireLeadershipAsync(name, owner, lease, cancellationToken);

        public ValueTask ReleaseLeadershipAsync(string name, string owner, CancellationToken cancellationToken) => _inner.ReleaseLeadershipAsync(name, owner, cancellationToken);

        public ValueTask<IReadOnlyList<ClaimedJob>> ClaimAsync(string queue, int maxCount, TimeSpan lease, CancellationToken cancellationToken) => _inner.ClaimAsync(queue, maxCount, lease, cancellationToken);

        public ValueTask<int> CompleteAsync(IReadOnlyList<JobLease> leases, CancellationToken cancellationToken) => _inner.CompleteAsync(leases, cancellationToken);

        public ValueTask<bool> RetryAsync(JobLease lease, TimeSpan delay, string error, CancellationToken cancellationToken) => _inner.RetryAsync(lease, delay, error, cancellationToken);

        public ValueTask<bool> DeadLetterAsync(JobLease lease, string error, CancellationToken cancellationToken) => _inner.DeadLetterAsync(lease, error, cancellationToken);

        public ValueTask<bool> ReleaseAsync(JobLease lease, TimeSpan delay, CancellationToken cancellationToken) => _inner.ReleaseAsync(lease, delay, cancellationToken);

        public ValueTask<int> RequeueAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken) => _inner.RequeueAsync(ids, cancellationToken);

        public ValueTask<int> ExtendLeasesAsync(IReadOnlyList<JobLease> leases, TimeSpan lease, CancellationToken cancellationToken) => _inner.ExtendLeasesAsync(leases, lease, cancellationToken);

        public ValueTask<JobSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken) => _inner.GetAsync(id, cancellationToken);
    }
}
