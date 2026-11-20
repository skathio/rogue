using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using Xunit;

namespace SkathIO.Rogue.Messaging.RabbitMQ.Tests;

/// <summary>
/// A fact that needs a RabbitMQ container: skipped on a machine with no reachable Docker, unless it
/// runs in CI, where a missing Docker must fail the run rather than pass it silently.
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
        InCi || DockerReachable() ? null : "No Docker is reachable for a RabbitMQ container. CI always runs these tests.";

    private static bool InCi =>
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);

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
            @"\\.\pipe\docker_engine",
        };
        return sockets.Any(static s => s.Length > 0 && File.Exists(s) && (s.StartsWith(@"\\.\pipe\", StringComparison.Ordinal) || Accepts(s)));
    }

    // A socket file can exist while this user may not connect to it (not in the docker group), and the
    // container would then fail every test in the collection instead of skipping them.
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
