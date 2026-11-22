using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue.Jobs.SqlServer;
using Testcontainers.MsSql;
using Xunit;

namespace SkathIO.Rogue.Consumer.Jobs.Tests;

/// <summary>The jobs scenarios on SkathIO.Rogue.Jobs.SqlServer, each test in a schema of its own.</summary>
[RequiresDocker]
public sealed class SqlServerJobsTests(SqlServerContainerFixture database) : JobsScenarioTests, IClassFixture<SqlServerContainerFixture>
{
    protected override void AddStore(IServiceCollection services) =>
        services.AddRogueSqlServerJobs(database.ConnectionString, static options => options.Schema = "t_" + Guid.NewGuid().ToString("N"));
}

/// <summary>One SQL Server container for the class; none is started when Docker is missing outside CI.</summary>
public sealed class SqlServerContainerFixture : IAsyncLifetime
{
    private MsSqlContainer? _container;

    public string ConnectionString => _container?.GetConnectionString() ?? throw new InvalidOperationException("No database: " + DockerGate.Skip);

    public async Task InitializeAsync()
    {
        if (DockerGate.Skip is not null)
        {
            return;
        }
        _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
