using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace SkathIO.Rogue.Jobs.Postgres.Tests;

/// <summary>The store's migrations and its use of the caller's transaction, checked without a database.</summary>
public sealed class PostgresSqlTests
{
    [Fact]
    public void Migrations_AreNumberedOneToThree_TheInboxLast()
    {
        var sql = new PostgresSql("rogue_jobs");

        Assert.Equal(3, sql.Migrations.Length);
        Assert.StartsWith("CREATE TABLE \"rogue_jobs\".inbox", sql.Migrations[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnqueueInATransaction_RefusesOneOfAnotherClient_AndNull()
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create("Host=localhost");
        ITransactionalJobStore store = new PostgresJobStore(dataSource);
        NewJob[] jobs = { new NewJob { Id = Guid.NewGuid(), Kind = "k", Queue = "q", Payload = new byte[] { 1 }, MaxAttempts = 1 } };

        await Assert.ThrowsAsync<ArgumentException>(async () => await store.EnqueueAsync(jobs, new OtherTransaction(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await store.EnqueueAsync(jobs, null!, CancellationToken.None));
    }

    private sealed class OtherTransaction : DbTransaction
    {
        public override System.Data.IsolationLevel IsolationLevel => System.Data.IsolationLevel.ReadCommitted;

        protected override DbConnection? DbConnection => null;

        public override void Commit()
        {
        }

        public override void Rollback()
        {
        }
    }
}
