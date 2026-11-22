using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SkathIO.Rogue.Jobs.Postgres;
using Testcontainers.PostgreSql;
using Xunit;

namespace SkathIO.Rogue.Consumer.Jobs.Tests;

/// <summary>The jobs scenarios on SkathIO.Rogue.Jobs.Postgres, each test in a schema of its own.</summary>
[RequiresDocker]
public sealed class PostgresJobsTests(PostgresContainerFixture database) : JobsScenarioTests, IClassFixture<PostgresContainerFixture>
{
    protected override void AddStore(IServiceCollection services) =>
        services.AddRoguePostgresJobs(database.DataSource, static options => options.Schema = "t_" + Guid.NewGuid().ToString("N"));
}

/// <summary>One Postgres container for the class; none is started when Docker is missing outside CI.</summary>
public sealed class PostgresContainerFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private NpgsqlDataSource? _dataSource;

    public NpgsqlDataSource DataSource => _dataSource ?? throw new InvalidOperationException("No database: " + DockerGate.Skip);

    public async Task InitializeAsync()
    {
        if (DockerGate.Skip is not null)
        {
            return;
        }
        _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await _container.StartAsync();
        _dataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
