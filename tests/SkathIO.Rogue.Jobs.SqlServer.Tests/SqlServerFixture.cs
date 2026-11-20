using System;
using System.Threading.Tasks;
using SkathIO.Rogue.Jobs.StoreContract;
using Testcontainers.MsSql;
using Xunit;

namespace SkathIO.Rogue.Jobs.SqlServer.Tests;

/// <summary>
/// One SQL Server container for the whole collection. Without Docker the collection's tests are
/// skipped (except in CI, where they fail), so no container is started.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private MsSqlContainer? _container;

    public string ConnectionString => _container?.GetConnectionString() ?? throw new InvalidOperationException("No database: " + TestDatabase.SkipReason());

    /// <summary>A schema no other test uses, so each test sees only its own tables.</summary>
    public static string NewSchema() => "t_" + Guid.NewGuid().ToString("N");

    public async Task InitializeAsync()
    {
        if (TestDatabase.SkipReason() is not null)
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

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sqlserver";
}
