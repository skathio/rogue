using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SkathIO.Rogue.Jobs.StoreContract;
using SkathIO.Rogue.Messaging;
using SkathIO.Rogue.Messaging.Outbox;
using Xunit;

namespace SkathIO.Rogue.Jobs.Postgres.Tests;

/// <summary>The Postgres store against the shared inbox contract, every test in one schema.</summary>
[Collection(PostgresCollection.Name)]
[RequiresDocker]
public sealed class PostgresMessageInboxStoreContractTests : MessageInboxStoreContract
{
    private readonly PostgresFixture _database;

    public PostgresMessageInboxStoreContractTests(PostgresFixture database) => _database = database;

    protected override Task<IMessageInboxStore> CreateInboxAsync(TimeProvider clock)
        => Task.FromResult<IMessageInboxStore>(new PostgresJobStore(_database.DataSource, new PostgresJobsOptions { Schema = "rogue_inbox_contract" }, clock));
}

/// <summary>
/// The outbox and inbox on Postgres: a message published in the caller's NpgsqlTransaction is a job row
/// relayed once the transaction commits and never if it rolls back, and a message delivered twice runs
/// its handler once.
/// </summary>
[Collection(PostgresCollection.Name)]
[RequiresDocker]
public sealed class PostgresOutboxTests
{
    private readonly PostgresFixture _database;
    private readonly string _schema = PostgresFixture.NewSchema();

    public PostgresOutboxTests(PostgresFixture database) => _database = database;

    private static CancellationToken None => CancellationToken.None;

    [StoreFact]
    public async Task PublishInTheCallersTransaction_SendsNothing_WhenItRollsBack()
    {
        var transport = new InMemoryTransport();
        await using ServiceProvider services = Host(transport);

        await using (NpgsqlConnection connection = await _database.DataSource.OpenConnectionAsync())
        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync())
        {
            await services.GetRequiredService<IMessageOutbox>().Publish(new PgOrderPlaced(1), transaction);
            await transaction.RollbackAsync();
        }
        int relayed = await services.GetRequiredService<JobWorker>().RunOnceAsync();

        Assert.Equal(0, relayed);
        Assert.Equal(0L, await CountJobsAsync());
        Assert.Empty(await transport.ReceiveAsync(100, None));
    }

    [StoreFact]
    public async Task PublishInTheCallersTransaction_IsRelayedWithItsId_OnceItCommits()
    {
        var transport = new InMemoryTransport();
        await using ServiceProvider services = Host(transport);
        JobWorker worker = services.GetRequiredService<JobWorker>();

        string id;
        int relayedBeforeCommit;
        await using (NpgsqlConnection connection = await _database.DataSource.OpenConnectionAsync())
        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync())
        {
            id = await services.GetRequiredService<IMessageOutbox>().Publish(new PgOrderPlaced(2), transaction);
            relayedBeforeCommit = await worker.RunOnceAsync();
            await transaction.CommitAsync();
        }
        int relayed = await worker.RunOnceAsync();

        Assert.Equal(0, relayedBeforeCommit);
        Assert.Equal(1, relayed);
        MessageEnvelope sent = Assert.Single(await transport.ReceiveAsync(100, None)).Envelope;
        Assert.Equal(id, sent.Id);
        Assert.Equal("pg.order-placed", sent.Name);
        Assert.Equal(new PgOrderPlaced(2), JsonSerializer.Deserialize(sent.Body.Span, PgOutboxJson.Default.PgOrderPlaced));
    }

    [StoreFact]
    public async Task DuplicateDelivery_RunsTheHandlerOnce()
    {
        var transport = new InMemoryTransport();
        await using ServiceProvider services = Host(transport);
        MessageEnvelope envelope = services.GetRequiredService<IMessageBus>().CreateEnvelope(new PgOrderPlaced(3));

        await transport.SendAsync(new[] { envelope, envelope }, None);
        await services.GetRequiredService<MessageConsumer>().RunOnceAsync();

        Assert.Equal(new[] { 3 }, services.GetRequiredService<PgOrders>().Handled);
        Assert.Empty(transport.DeadLettered);
        Assert.Empty(await transport.ReceiveAsync(100, None));
    }

    private ServiceProvider Host(InMemoryTransport transport)
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<PgOrders>();
        collection.AddSingleton<Ran>();
        collection.AddSingleton<IMessageTransport>(transport);
        collection.AddRogue();
        collection.AddRogueMessaging(static o =>
        {
            o.SerializerContext = PgOutboxJson.Default;
            o.MaxConcurrency = 1;
        });
        collection.AddRogueJobs(static o => o.SerializerContext = PostgresTestJson.Default);
        collection.AddRoguePostgresJobs(_database.DataSource, o => o.Schema = _schema);
        collection.AddRogueMessagingOutbox();
        return collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private async Task<long> CountJobsAsync()
    {
        await using NpgsqlCommand command = _database.DataSource.CreateCommand("SELECT count(*) FROM \"" + _schema + "\".jobs");
        return (long)(await command.ExecuteScalarAsync())!;
    }
}

/// <summary>The orders the handler saw.</summary>
public sealed class PgOrders
{
    private readonly ConcurrentQueue<int> _handled = new ConcurrentQueue<int>();

    public IReadOnlyCollection<int> Handled => _handled;

    public void Add(int id) => _handled.Enqueue(id);
}

[Message("pg.order-placed")]
public sealed record PgOrderPlaced(int Id) : IEvent;

public sealed class PgOrderPlacedHandler : IEventHandler<PgOrderPlaced>
{
    private readonly PgOrders _orders;

    public PgOrderPlacedHandler(PgOrders orders) => _orders = orders;

    public ValueTask Handle(PgOrderPlaced e, CancellationToken cancellationToken)
    {
        _orders.Add(e.Id);
        return default;
    }
}

[JsonSerializable(typeof(PgOrderPlaced))]
internal sealed partial class PgOutboxJson : JsonSerializerContext
{
}
