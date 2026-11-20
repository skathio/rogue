using System;
using System.Data.Common;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SkathIO.Rogue.Jobs.SqlServer.Tests;

/// <summary>The store's SQL and its use of the caller's transaction, checked without a database.</summary>
public sealed class SqlServerSqlTests
{
    private const string ConnectionString = "Server=localhost;Database=jobs;TrustServerCertificate=True";

    [Fact]
    public void Migrations_CreateEachTableTypeOnce()
    {
        // A table type cannot be created again or altered: a second CREATE TYPE of one name fails the
        // migration, and with it the store's first command on a fresh database.
        var sql = new SqlServerSql("rogue_jobs");

        string[] created = sql.Migrations
            .SelectMany(static m => Regex.Matches(m, @"CREATE TYPE (\[[^\]]+\]\.\[[^\]]+\])").Select(static c => c.Groups[1].Value))
            .ToArray();

        Assert.Equal(new[] { "[rogue_jobs].[job_batch]", "[rogue_jobs].[lease_rows]", "[rogue_jobs].[id_rows]" }, created);
    }

    [Fact]
    public void JobBatchType_CarriesEveryColumnTheInsertReads()
    {
        var sql = new SqlServerSql("rogue_jobs");
        string type = Regex.Match(string.Join("\n", sql.Migrations), @"CREATE TYPE \[rogue_jobs\]\.\[job_batch\] AS TABLE \((?<columns>[^']*)\)").Groups["columns"].Value;

        foreach (string column in new[] { "ord", "id", "kind", "queue", "payload", "max_attempts", "run_at" })
        {
            Assert.Matches(@"\b" + column + @" ", type);
        }
    }

    [Fact]
    public void Migrations_AreNumberedOneToThree_TheInboxLast()
    {
        var sql = new SqlServerSql("rogue_jobs");

        Assert.Equal(3, sql.Migrations.Length);
        Assert.Contains("version = 3", sql.Migrations[2], StringComparison.Ordinal);
        Assert.Contains("[rogue_jobs].[inbox]", sql.Migrations[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnqueueInATransaction_RefusesOneOfAnotherClient_AndNull()
    {
        ITransactionalJobStore store = new SqlServerJobStore(ConnectionString);
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
