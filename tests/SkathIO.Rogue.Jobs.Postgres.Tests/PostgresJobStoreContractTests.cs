using System;
using System.Threading.Tasks;
using SkathIO.Rogue.Jobs.StoreContract;
using Xunit;

namespace SkathIO.Rogue.Jobs.Postgres.Tests;

/// <summary>The Postgres store against the shared store contract, every test in one schema.</summary>
[Collection(PostgresCollection.Name)]
[RequiresDocker]
public sealed class PostgresJobStoreContractTests : JobStoreContract
{
    private readonly PostgresFixture _database;

    public PostgresJobStoreContractTests(PostgresFixture database) => _database = database;

    protected override Task<IJobStore> CreateStoreAsync(TimeProvider clock)
        => Task.FromResult<IJobStore>(new PostgresJobStore(_database.DataSource, new PostgresJobsOptions { Schema = "rogue_jobs_contract" }, clock));
}
