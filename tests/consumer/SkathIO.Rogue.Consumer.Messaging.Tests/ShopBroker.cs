using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using DotNet.Testcontainers.Containers;
using Npgsql;
using RabbitMQ.Client;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace SkathIO.Rogue.Consumer.Messaging.Tests;

/// <summary>
/// A fact that needs the broker and database containers: skipped on a machine with no reachable
/// Docker, but never in CI, where a missing Docker must fail the run rather than pass it silently.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RabbitMqFactAttribute : FactAttribute
{
    public RabbitMqFactAttribute()
    {
        if (SkipReason() is { } reason)
        {
            Skip = reason;
        }
    }

    /// <summary>Why the container tests are skipped here, or null when they run.</summary>
    public static string? SkipReason() =>
        InCi || DockerReachable() ? null : "No Docker is reachable for the RabbitMQ and Postgres containers. CI always runs these tests, and fails without Docker.";

    // GitHub Actions sets both variables.
    private static bool InCi =>
        IsTrue(Environment.GetEnvironmentVariable("CI")) || IsTrue(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));

    private static bool IsTrue(string? value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    // Testcontainers' own discovery is wider; this covers the usual places, and CI never skips.
    private static bool DockerReachable()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOCKER_HOST")))
        {
            return true;
        }
        string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] sockets =
        {
            "/var/run/docker.sock",
            runtime is null ? string.Empty : Path.Combine(runtime, "docker.sock"),
            Path.Combine(home, ".docker", "run", "docker.sock"),
            Path.Combine(home, ".docker", "desktop", "docker.sock"),
        };
        return sockets.Any(static s => s.Length > 0 && File.Exists(s) && Accepts(s));
    }

    // A socket file can exist while this user may not connect to it (not in the docker group), and the
    // containers would then fail every test instead of skipping them.
    private static bool Accepts(string socketPath)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}

/// <summary>
/// One RabbitMQ and one Postgres container for the test class; none is started when the tests are
/// skipped. Each test gets a virtual host of its own (<see cref="NewShopAsync"/>), so the services keep
/// their fixed queue names without meeting another test's messages.
/// </summary>
public sealed class ShopBroker : IAsyncLifetime
{
    private RabbitMqContainer? _rabbitMq;
    private PostgreSqlContainer? _postgres;
    private NpgsqlDataSource? _dataSource;

    public async Task InitializeAsync()
    {
        if (RabbitMqFactAttribute.SkipReason() is not null)
        {
            return;
        }
        _rabbitMq = new RabbitMqBuilder("rabbitmq:4.1-alpine").Build();
        _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await Task.WhenAll(_rabbitMq.StartAsync(), _postgres.StartAsync());
        _dataSource = NpgsqlDataSource.Create(_postgres.GetConnectionString());
    }

    /// <summary>A new virtual host, and a run id that keeps the test's database schemas apart.</summary>
    public async Task<Shop> NewShopAsync()
    {
        RabbitMqContainer rabbitMq = _rabbitMq ?? throw new InvalidOperationException(RabbitMqFactAttribute.SkipReason());
        string run = Guid.NewGuid().ToString("N");
        var broker = new Uri(rabbitMq.GetConnectionString());
        string user = Uri.UnescapeDataString(broker.UserInfo.Split(':')[0]);
        await RabbitMqCtlAsync("add_vhost", run);
        await RabbitMqCtlAsync("set_permissions", "-p", run, user, ".*", ".*", ".*");
        var vhost = new UriBuilder(broker) { Path = "/" + run }.Uri;
        return new Shop(this, run, vhost, _dataSource!);
    }

    internal Task DeleteVirtualHostAsync(string name) => RabbitMqCtlAsync("delete_vhost", name);

    public async Task DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }
        if (_rabbitMq is not null)
        {
            await _rabbitMq.DisposeAsync();
        }
        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    private async Task RabbitMqCtlAsync(params string[] arguments)
    {
        ExecResult result = await _rabbitMq!.ExecAsync(new[] { "rabbitmqctl" }.Concat(arguments).ToArray());
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("rabbitmqctl " + arguments[0] + " failed: " + result.Stderr);
        }
    }
}

/// <summary>
/// One test's share of the containers: a virtual host on the broker, deleted with everything in it at
/// the end, and the run id that names the services' database schemas.
/// </summary>
public sealed class Shop : IAsyncDisposable
{
    private readonly ShopBroker _owner;

    internal Shop(ShopBroker owner, string run, Uri broker, NpgsqlDataSource dataSource)
    {
        _owner = owner;
        Run = run;
        Broker = broker;
        DataSource = dataSource;
    }

    public string Run { get; }

    /// <summary>The broker, at this test's virtual host.</summary>
    public Uri Broker { get; }

    public NpgsqlDataSource DataSource { get; }

    /// <summary>How many messages wait in <paramref name="queue"/>, read as an operator would, on a connection of its own.</summary>
    public async Task<uint> MessagesInAsync(string queue)
    {
        await using IConnection connection = await new ConnectionFactory { Uri = Broker }.CreateConnectionAsync();
        await using IChannel channel = await connection.CreateChannelAsync();
        return await channel.MessageCountAsync(queue);
    }

    /// <summary>Takes the next message from <paramref name="queue"/>, or null when it is empty.</summary>
    public async Task<BasicGetResult?> TakeAsync(string queue)
    {
        await using IConnection connection = await new ConnectionFactory { Uri = Broker }.CreateConnectionAsync();
        await using IChannel channel = await connection.CreateChannelAsync();
        return await channel.BasicGetAsync(queue, true);
    }

    public async ValueTask DisposeAsync() => await _owner.DeleteVirtualHostAsync(Run);
}
