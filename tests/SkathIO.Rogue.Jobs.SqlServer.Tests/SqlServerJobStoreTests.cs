using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using SkathIO.Rogue.Jobs.StoreContract;
using Xunit;

namespace SkathIO.Rogue.Jobs.SqlServer.Tests;

/// <summary>What only the SQL Server store does: migrations under an app lock, READPAST claims, the poller's probe, pruning.</summary>
[Collection(SqlServerCollection.Name)]
[RequiresDocker]
public sealed class SqlServerJobStoreTests
{
    // Guards against a hang only; no assertion depends on how long anything takes.
    private static readonly TimeSpan _hang = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _lease = TimeSpan.FromMinutes(1);

    private readonly SqlServerFixture _database;
    private readonly ManualTimeProvider _clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly string _schema = SqlServerFixture.NewSchema();

    public SqlServerJobStoreTests(SqlServerFixture database) => _database = database;

    private static CancellationToken None => CancellationToken.None;

    [StoreFact]
    public async Task Migrate_RunsEachMigrationOnce_EvenWhenProcessesRaceForIt()
    {
        SqlServerJobStore[] stores = Enumerable.Range(0, 4).Select(_ => Store(static o => o.AutoMigrate = false)).ToArray();

        await Task.WhenAll(stores.Select(static s => s.MigrateAsync()));
        await stores[0].MigrateAsync();

        Assert.Equal(3, await ScalarAsync<int>("SELECT count(*) FROM [" + _schema + "].[schema_version]"));
        Assert.Equal(3, await ScalarAsync<int>("SELECT max(version) FROM [" + _schema + "].[schema_version]"));
    }

    [StoreFact]
    public async Task WithoutAutoMigrate_TheStoreFailsUntilMigrated()
    {
        SqlServerJobStore store = Store(static o => o.AutoMigrate = false);

        await Assert.ThrowsAsync<SqlException>(async () => await store.GetAsync(Guid.NewGuid(), None));
        await store.MigrateAsync();

        Assert.Null(await store.GetAsync(Guid.NewGuid(), None));
    }

    [StoreFact]
    public async Task Claim_SkipsARowAnotherTransactionHolds_InsteadOfWaitingForIt()
    {
        SqlServerJobStore store = Store();
        NewJob held = Job();
        NewJob free = Job();
        await store.EnqueueAsync(new[] { held, free }, None);

        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using SqlTransaction transaction = connection.BeginTransaction();
        await using (var lockRow = new SqlCommand("UPDATE [" + _schema + "].[jobs] SET kind = kind WHERE id = @id", connection, transaction))
        {
            lockRow.Parameters.AddWithValue("@id", held.Id);
            Assert.Equal(1, await lockRow.ExecuteNonQueryAsync());
        }
        IReadOnlyList<ClaimedJob> claimed = await store.ClaimAsync("q", 10, _lease, None).AsTask().WaitAsync(_hang);
        await transaction.RollbackAsync();

        Assert.Equal(new[] { free.Id }, claimed.Select(static j => j.Id));
        Assert.Equal(new[] { held.Id }, (await store.ClaimAsync("q", 10, _lease, None)).Select(static j => j.Id));
    }

    [StoreFact]
    public async Task Probe_FindsAJob_OnlyInTheWindowItBecameDueIn()
    {
        SqlServerJobStore store = Store();
        DateTime start = store.Now();
        await store.EnqueueAsync(new[] { Job() }, None);
        ClaimedJob claimed = Assert.Single(await store.ClaimAsync("q", 1, _lease, None));
        Assert.True(await store.RetryAsync(claimed.Lease, TimeSpan.FromSeconds(30), "e", None));
        DateTime due = start + TimeSpan.FromSeconds(30);

        Assert.False(await store.HasNewlyDueAsync(start, due - TimeSpan.FromTicks(1), None));
        Assert.True(await store.HasNewlyDueAsync(start, due, None));
        Assert.False(await store.HasNewlyDueAsync(due, due + TimeSpan.FromMinutes(1), None));
    }

    [StoreFact]
    public async Task Probe_FindsAScheduledJob_OnceItsTimeComes()
    {
        SqlServerJobStore store = Store();
        DateTime start = store.Now();
        DateTime due = start + TimeSpan.FromMinutes(5);
        NewJob scheduled = Job();
        await store.EnqueueAsync(new[] { new NewJob { Id = scheduled.Id, Kind = scheduled.Kind, Queue = scheduled.Queue, Payload = scheduled.Payload, MaxAttempts = 1, RunAt = new DateTimeOffset(due, TimeSpan.Zero) } }, None);

        Assert.False(await store.HasNewlyDueAsync(start, due - TimeSpan.FromTicks(1), None));
        Assert.True(await store.HasNewlyDueAsync(start, due, None));
    }

    [StoreFact]
    public async Task Prune_DeletesOnlyFinishedJobsPastTheirRetention()
    {
        SqlServerJobStore store = Store(static o =>
        {
            o.SucceededRetention = TimeSpan.FromHours(1);
            o.DeadLetteredRetention = TimeSpan.FromHours(2);
            o.PruneBatchSize = 1;
        });
        NewJob succeeded = Job();
        NewJob deadLettered = Job();
        NewJob waiting = Job();
        await store.EnqueueAsync(new[] { succeeded, deadLettered }, None);
        IReadOnlyList<ClaimedJob> claimed = await store.ClaimAsync("q", 2, _lease, None);
        Assert.Equal(1, await store.CompleteAsync(new[] { claimed.Single(c => c.Id == succeeded.Id).Lease }, None));
        Assert.True(await store.DeadLetterAsync(claimed.Single(c => c.Id == deadLettered.Id).Lease, "poison", None));
        await store.EnqueueAsync(new[] { waiting }, None);

        _clock.Advance(TimeSpan.FromHours(1));
        int early = await store.PruneAsync();
        _clock.Advance(TimeSpan.FromTicks(1));
        int first = await store.PruneAsync();
        _clock.Advance(TimeSpan.FromHours(1));
        int second = await store.PruneAsync();

        Assert.Equal((0, 1, 1), (early, first, second));
        Assert.Null(await store.GetAsync(succeeded.Id, None));
        Assert.Null(await store.GetAsync(deadLettered.Id, None));
        Assert.Equal(JobState.Enqueued, (await store.GetAsync(waiting.Id, None))!.State);
    }

    private SqlServerJobStore Store(Action<SqlServerJobsOptions>? configure = null)
    {
        var options = new SqlServerJobsOptions { Schema = _schema };
        configure?.Invoke(options);
        return new SqlServerJobStore(_database.ConnectionString, options, _clock);
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static NewJob Job(string queue = "q")
        => new NewJob { Id = Guid.NewGuid(), Kind = "sqlserver.test", Queue = queue, Payload = Encoding.UTF8.GetBytes("{}"), MaxAttempts = 3 };
}
