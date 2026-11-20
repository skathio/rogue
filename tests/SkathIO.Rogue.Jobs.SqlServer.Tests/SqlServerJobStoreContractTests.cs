using System;
using System.Threading.Tasks;
using SkathIO.Rogue.Jobs.StoreContract;
using Xunit;

namespace SkathIO.Rogue.Jobs.SqlServer.Tests;

/// <summary>The SQL Server store against the shared store contract, every test in one schema.</summary>
[Collection(SqlServerCollection.Name)]
[RequiresDocker]
public sealed class SqlServerJobStoreContractTests : JobStoreContract
{
    private readonly SqlServerFixture _database;

    public SqlServerJobStoreContractTests(SqlServerFixture database) => _database = database;

    protected override Task<IJobStore> CreateStoreAsync(TimeProvider clock)
        => Task.FromResult<IJobStore>(new SqlServerJobStore(_database.ConnectionString, new SqlServerJobsOptions { Schema = "rogue_jobs_contract" }, clock));
}
