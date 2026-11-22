using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue.Jobs.StoreContract;
using SkathIO.Rogue.Messaging;
using SkathIO.Rogue.Messaging.Outbox;
using Xunit;

namespace SkathIO.Rogue.Jobs.SqlServer.Tests;

/// <summary>The SQL Server store against the shared inbox contract, every test in one schema.</summary>
[Collection(SqlServerCollection.Name)]
[RequiresDocker]
public sealed class SqlServerMessageInboxStoreContractTests : MessageInboxStoreContract
{
    private readonly SqlServerFixture _database;

    public SqlServerMessageInboxStoreContractTests(SqlServerFixture database) => _database = database;

    protected override Task<IMessageInboxStore> CreateInboxAsync(TimeProvider clock)
        => Task.FromResult<IMessageInboxStore>(new SqlServerJobStore(_database.ConnectionString, new SqlServerJobsOptions { Schema = "rogue_inbox_contract" }, clock));
}

/// <summary>
/// The outbox and inbox on SQL Server: a message published in the caller's SqlTransaction is a job row
/// relayed once the transaction commits and never if it rolls back, and a message delivered twice runs
/// its handler once.
/// </summary>
[Collection(SqlServerCollection.Name)]
[RequiresDocker]
public sealed class SqlServerOutboxTests
{
    private readonly SqlServerFixture _database;
    private readonly string _schema = SqlServerFixture.NewSchema();

    public SqlServerOutboxTests(SqlServerFixture database) => _database = database;

    private static CancellationToken None => CancellationToken.None;

    [StoreFact]
    public async Task PublishInTheCallersTransaction_SendsNothing_WhenItRollsBack()
    {
        var transport = new InMemoryTransport();
        await using ServiceProvider services = Host(transport);

        await using (var connection = new SqlConnection(_database.ConnectionString))
        {
            await connection.OpenAsync();
            await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync();
            await services.GetRequiredService<IMessageOutbox>().Publish(new SqlOrderPlaced(1), transaction);
            await transaction.RollbackAsync();
        }
        int relayed = await services.GetRequiredService<JobWorker>().RunOnceAsync();

        Assert.Equal(0, relayed);
        Assert.Equal(0, await CountJobsAsync());
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
        await using (var connection = new SqlConnection(_database.ConnectionString))
        {
            await connection.OpenAsync();
            await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync();
            id = await services.GetRequiredService<IMessageOutbox>().Publish(new SqlOrderPlaced(2), transaction);
            relayedBeforeCommit = await worker.RunOnceAsync();
            await transaction.CommitAsync();
        }
        int relayed = await worker.RunOnceAsync();

        Assert.Equal(0, relayedBeforeCommit);
        Assert.Equal(1, relayed);
        MessageEnvelope sent = Assert.Single(await transport.ReceiveAsync(100, None)).Envelope;
        Assert.Equal(id, sent.Id);
        Assert.Equal("sql.order-placed", sent.Name);
        Assert.Equal(new SqlOrderPlaced(2), JsonSerializer.Deserialize(sent.Body.Span, SqlOutboxJson.Default.SqlOrderPlaced));
    }

    [StoreFact]
    public async Task PublishOnACompletedTransaction_IsRefused()
    {
        var transport = new InMemoryTransport();
        await using ServiceProvider services = Host(transport);
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await transaction.CommitAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await services.GetRequiredService<IMessageOutbox>().Publish(new SqlOrderPlaced(4), transaction));

        Assert.Equal(0, await services.GetRequiredService<JobWorker>().RunOnceAsync());
    }

    [StoreFact]
    public async Task DuplicateDelivery_RunsTheHandlerOnce()
    {
        var transport = new InMemoryTransport();
        await using ServiceProvider services = Host(transport);
        MessageEnvelope envelope = services.GetRequiredService<IMessageBus>().CreateEnvelope(new SqlOrderPlaced(3));

        await transport.SendAsync(new[] { envelope, envelope }, None);
        await services.GetRequiredService<MessageConsumer>().RunOnceAsync();

        Assert.Equal(new[] { 3 }, services.GetRequiredService<SqlOrders>().Handled);
        Assert.Empty(transport.DeadLettered);
        Assert.Empty(await transport.ReceiveAsync(100, None));
    }

    private ServiceProvider Host(InMemoryTransport transport)
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<SqlOrders>();
        collection.AddSingleton<IMessageTransport>(transport);
        collection.AddRogue();
        collection.AddRogueMessaging(static o =>
        {
            o.SerializerContext = SqlOutboxJson.Default;
            o.MaxConcurrency = 1;
        });
        collection.AddRogueJobs();
        collection.AddRogueSqlServerJobs(_database.ConnectionString, o => o.Schema = _schema);
        collection.AddRogueMessagingOutbox();
        return collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private async Task<int> CountJobsAsync()
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT count(*) FROM [" + _schema + "].[jobs]", connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }
}

/// <summary>The orders the handler saw.</summary>
public sealed class SqlOrders
{
    private readonly ConcurrentQueue<int> _handled = new ConcurrentQueue<int>();

    public IReadOnlyCollection<int> Handled => _handled;

    public void Add(int id) => _handled.Enqueue(id);
}

[Message("sql.order-placed")]
public sealed record SqlOrderPlaced(int Id) : IEvent;

public sealed class SqlOrderPlacedHandler : IEventHandler<SqlOrderPlaced>
{
    private readonly SqlOrders _orders;

    public SqlOrderPlacedHandler(SqlOrders orders) => _orders = orders;

    public ValueTask Handle(SqlOrderPlaced e, CancellationToken cancellationToken)
    {
        _orders.Add(e.Id);
        return default;
    }
}

[JsonSerializable(typeof(SqlOrderPlaced))]
internal sealed partial class SqlOutboxJson : JsonSerializerContext
{
}
