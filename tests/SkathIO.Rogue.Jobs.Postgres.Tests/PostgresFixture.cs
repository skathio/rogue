using System;
using System.Threading.Tasks;
using Npgsql;
using SkathIO.Rogue.Jobs.StoreContract;
using Testcontainers.PostgreSql;
using Xunit;

namespace SkathIO.Rogue.Jobs.Postgres.Tests;

/// <summary>
/// One Postgres for the whole collection: a container, or the database named by
/// <see cref="TestDatabase.PostgresVariable"/>. Without either the collection's tests are skipped, so
/// no container is started.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private NpgsqlDataSource? _dataSource;

    public NpgsqlDataSource DataSource => _dataSource ?? throw new InvalidOperationException("No database: " + TestDatabase.SkipReason());

    /// <summary>A schema no other test uses, so each test sees only its own tables.</summary>
    public static string NewSchema() => "t_" + Guid.NewGuid().ToString("N");

    public async Task InitializeAsync()
    {
        string? connectionString = TestDatabase.PostgresConnectionString;
        if (connectionString is null)
        {
            if (TestDatabase.SkipReason() is not null)
            {
                return;
            }
            _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await _container.StartAsync();
            connectionString = _container.GetConnectionString();
        }
        _dataSource = NpgsqlDataSource.Create(connectionString);
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

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
