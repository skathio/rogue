using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SkathIO.Rogue.Jobs.StoreContract;
using Xunit;

namespace SkathIO.Rogue.Jobs.Postgres.Tests;

/// <summary>What only the Postgres store does: migrations, the caller's transaction, NOTIFY, pruning, batched completions.</summary>
[Collection(PostgresCollection.Name)]
[RequiresDocker]
public sealed class PostgresJobStoreTests
{
    // Guards against a hang only; no assertion depends on how long anything takes.
    private static readonly TimeSpan _hang = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _lease = TimeSpan.FromMinutes(1);

    private readonly PostgresFixture _database;
    private readonly ManualTimeProvider _clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly string _schema = PostgresFixture.NewSchema();

    public PostgresJobStoreTests(PostgresFixture database) => _database = database;

    private static CancellationToken None => CancellationToken.None;

    [StoreFact]
    public async Task Migrate_RunsEachMigrationOnce_EvenWhenProcessesRaceForIt()
    {
        PostgresJobStore[] stores = Enumerable.Range(0, 4).Select(_ => Store(static o => o.AutoMigrate = false)).ToArray();

        await Task.WhenAll(stores.Select(static s => s.MigrateAsync()));
        await stores[0].MigrateAsync();

        Assert.Equal(3L, await ScalarAsync<long>("SELECT count(*) FROM \"" + _schema + "\".schema_version"));
        Assert.Equal(3, await ScalarAsync<int>("SELECT max(version) FROM \"" + _schema + "\".schema_version"));
    }

    [StoreFact]
    public async Task WithoutAutoMigrate_TheStoreFailsUntilMigrated()
    {
        PostgresJobStore store = Store(static o => o.AutoMigrate = false);

        await Assert.ThrowsAsync<PostgresException>(async () => await store.GetAsync(Guid.NewGuid(), None));
        await store.MigrateAsync();

        Assert.Null(await store.GetAsync(Guid.NewGuid(), None));
    }

    [StoreFact]
    public async Task EnqueueInTheCallersTransaction_StoresNothing_WhenItRollsBack()
    {
        PostgresJobStore store = Store();
        NewJob single = Job();
        NewJob[] batch = { Job(), Job(), Job() };

        await using (NpgsqlConnection connection = await _database.DataSource.OpenConnectionAsync())
        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync())
        {
            await store.EnqueueAsync(new[] { single }, transaction);
            await store.EnqueueAsync(batch, transaction);
            JobSnapshot? seenOutside = await store.GetAsync(single.Id, None);
            await transaction.RollbackAsync();
            Assert.Null(seenOutside);
        }

        foreach (NewJob job in batch.Append(single))
        {
            Assert.Null(await store.GetAsync(job.Id, None));
        }
    }

    [StoreFact]
    public async Task EnqueueInTheCallersTransaction_IsClaimable_OnceItCommits()
    {
        PostgresJobStore store = Store();
        NewJob[] jobs = { Job(), Job() };

        await using (NpgsqlConnection connection = await _database.DataSource.OpenConnectionAsync())
        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync())
        {
            await store.EnqueueAsync(jobs, connection);
            Assert.Empty(await store.ClaimAsync("q", 10, _lease, None));
            await transaction.CommitAsync();
        }

        Assert.Equal(jobs.Select(static j => j.Id), (await store.ClaimAsync("q", 10, _lease, None)).Select(static j => j.Id));
    }

    [StoreFact]
    public async Task EnqueueOnTheCallersConnection_RejectsAClosedConnection_AndACompletedTransaction()
    {
        PostgresJobStore store = Store();
        await using NpgsqlConnection connection = _database.DataSource.CreateConnection();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.EnqueueAsync(new[] { Job() }, connection));
        await connection.OpenAsync();
        NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await transaction.CommitAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.EnqueueAsync(new[] { Job() }, transaction));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await store.EnqueueAsync(new[] { Job() }, (NpgsqlConnection)null!));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await store.EnqueueAsync(new NewJob[] { null! }, connection));
    }

    [StoreFact]
    public async Task Enqueue_WakesAListener_InAnotherProcess()
    {
        PostgresJobStore store = Store();
        await store.MigrateAsync();
        var wakes = new WakeCounter();
        var listener = new PostgresJobListener(Store(), wakes.Wake, TimeProvider.System, NullLogger.Instance);
        await listener.StartAsync(None);

        await wakes.WaitForAsync(1).WaitAsync(_hang);
        await store.EnqueueAsync(new[] { Job() }, None);
        await wakes.WaitForAsync(2).WaitAsync(_hang);

        await listener.StopAsync(None);
    }

    [StoreFact]
    public async Task Listener_Reconnects_WhenItsConnectionIsKilled()
    {
        PostgresJobStore store = Store(static o => o.ListenReconnectDelay = TimeSpan.FromMilliseconds(1));
        await store.MigrateAsync();
        var wakes = new WakeCounter();
        var listener = new PostgresJobListener(store, wakes.Wake, TimeProvider.System, NullLogger.Instance);
        await listener.StartAsync(None);
        await wakes.WaitForAsync(1).WaitAsync(_hang);

        long killed = await ScalarAsync<long>("SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE query LIKE 'LISTEN \"" + _schema + "\"%'");
        Assert.Equal(1L, killed);
        await wakes.WaitForAsync(2).WaitAsync(_hang);
        await store.EnqueueAsync(new[] { Job() }, None);
        await wakes.WaitForAsync(3).WaitAsync(_hang);

        await listener.StopAsync(None);
    }

    [StoreFact]
    public async Task ConcurrentCompletions_EachCountTheirOwnLeases()
    {
        PostgresJobStore store = Store();
        NewJob[] jobs = Enumerable.Range(0, 40).Select(_ => Job()).ToArray();
        await store.EnqueueAsync(jobs, None);
        IReadOnlyList<ClaimedJob> claimed = await store.ClaimAsync("q", 40, _lease, None);

        int[] counts = await Task.WhenAll(claimed.Select(c => Task.Run(async () => await store.CompleteAsync(new[] { c.Lease, new JobLease(Guid.NewGuid(), 1) }, None))));
        int again = await store.CompleteAsync(claimed.Select(static c => c.Lease).ToArray(), None);

        Assert.All(counts, static c => Assert.Equal(1, c));
        Assert.Equal(0, again);
        foreach (NewJob job in jobs)
        {
            Assert.Equal(JobState.Succeeded, (await store.GetAsync(job.Id, None))!.State);
        }
    }

    [StoreFact]
    public async Task Prune_DeletesFinishedJobs_PastTheirRetention_AndNothingElse()
    {
        PostgresJobStore store = Store(static o =>
        {
            o.SucceededRetention = TimeSpan.FromHours(1);
            o.DeadLetteredRetention = TimeSpan.FromHours(2);
            o.PruneBatchSize = 1;
        });
        NewJob[] jobs = Enumerable.Range(0, 5).Select(_ => Job()).ToArray();
        await store.EnqueueAsync(jobs, None);
        IReadOnlyList<ClaimedJob> c = await store.ClaimAsync("q", 4, _lease, None);
        await store.CompleteAsync(new[] { c[0].Lease, c[1].Lease }, None);
        await store.DeadLetterAsync(c[2].Lease, "poison", None);

        _clock.Advance(TimeSpan.FromHours(1));
        int atTheLimit = await store.PruneAsync();
        _clock.Advance(TimeSpan.FromTicks(10));
        int pastSucceeded = await store.PruneAsync();
        _clock.Advance(TimeSpan.FromHours(1));
        int pastDeadLettered = await store.PruneAsync();

        Assert.Equal(0, atTheLimit);
        Assert.Equal(2, pastSucceeded);
        Assert.Equal(1, pastDeadLettered);
        Assert.Null(await store.GetAsync(jobs[0].Id, None));
        Assert.Null(await store.GetAsync(jobs[2].Id, None));
        Assert.Equal(JobState.Processing, (await store.GetAsync(jobs[3].Id, None))!.State);
        Assert.Equal(JobState.Enqueued, (await store.GetAsync(jobs[4].Id, None))!.State);
    }

    [StoreFact]
    public async Task Claim_RescuesAnOrphan_OnlyInItsOwnQueue()
    {
        PostgresJobStore store = Store();
        NewJob mine = Job("mine");
        NewJob other = Job("other");
        await store.EnqueueAsync(new[] { mine, other }, None);
        Assert.Single(await store.ClaimAsync("mine", 1, _lease, None));
        Assert.Single(await store.ClaimAsync("other", 1, _lease, None));

        _clock.Advance(_lease);
        ClaimedJob rescued = Assert.Single(await store.ClaimAsync("mine", 1, _lease, None));

        Assert.Equal(mine.Id, rescued.Id);
        Assert.Equal(2, rescued.Attempt);
        Assert.Equal(JobState.Processing, (await store.GetAsync(other.Id, None))!.State);
    }

    [StoreFact]
    public async Task TypedEnqueue_InTheCallersTransaction_RunsTheHandler_OnlyAfterCommit()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton(new Ran());
        services.AddRogue();
        services.AddRogueJobs(o => o.SerializerContext = PostgresTestJson.Default);
        services.AddRoguePostgresJobs(_database.DataSource, o => o.Schema = _schema);
        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        IPostgresJobScheduler scheduler = provider.GetRequiredService<IPostgresJobScheduler>();
        JobWorker worker = provider.GetRequiredService<JobWorker>();

        await using (NpgsqlConnection connection = await _database.DataSource.OpenConnectionAsync())
        {
            await using (NpgsqlTransaction rolledBack = await connection.BeginTransactionAsync())
            {
                await scheduler.Enqueue(new PostgresTestJob("lost"), rolledBack);
                await rolledBack.RollbackAsync();
            }
            await using NpgsqlTransaction committed = await connection.BeginTransactionAsync();
            await scheduler.EnqueueMany(new[] { new PostgresTestJob("a"), new PostgresTestJob("b") }, committed);
            await scheduler.Enqueue(new PostgresTestJob("c"), connection);
            await committed.CommitAsync();
        }
        int ran = await worker.RunOnceAsync();

        Assert.IsType<PostgresJobStore>(provider.GetRequiredService<IJobStore>());
        Assert.Equal(3, ran);
        Assert.Equal(new[] { "a", "b", "c" }, provider.GetRequiredService<Ran>().Values.OrderBy(static v => v, StringComparer.Ordinal));
    }

    private PostgresJobStore Store(Action<PostgresJobsOptions>? configure = null)
    {
        var options = new PostgresJobsOptions { Schema = _schema };
        configure?.Invoke(options);
        return new PostgresJobStore(_database.DataSource, options, _clock);
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using NpgsqlCommand command = _database.DataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static NewJob Job(string queue = "q")
        => new NewJob { Id = Guid.NewGuid(), Kind = "postgres.test", Queue = queue, Payload = Encoding.UTF8.GetBytes("{}"), MaxAttempts = 3 };

    /// <summary>Counts wake-ups and lets a test wait for the n-th.</summary>
    private sealed class WakeCounter
    {
        private readonly object _gate = new object();
        private readonly List<(int Count, TaskCompletionSource Reached)> _waiters = new List<(int, TaskCompletionSource)>();
        private int _count;

        public void Wake()
        {
            lock (_gate)
            {
                _count++;
                foreach ((int count, TaskCompletionSource reached) in _waiters)
                {
                    if (_count >= count)
                    {
                        reached.TrySetResult();
                    }
                }
            }
        }

        public Task WaitForAsync(int count)
        {
            lock (_gate)
            {
                var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_count >= count)
                {
                    reached.SetResult();
                }
                _waiters.Add((count, reached));
                return reached.Task;
            }
        }
    }
}
