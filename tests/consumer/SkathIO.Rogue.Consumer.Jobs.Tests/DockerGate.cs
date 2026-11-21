using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace SkathIO.Rogue.Consumer.Jobs.Tests;

/// <summary>
/// Whether the tests that need a database container can run here. Without a reachable Docker they
/// are skipped on a developer machine, but never in CI, where a missing Docker must fail the run
/// rather than pass it silently.
/// </summary>
internal static class DockerGate
{
    /// <summary>True in a CI run: GitHub Actions sets both variables.</summary>
    public static bool InCi =>
        IsTrue(Environment.GetEnvironmentVariable("CI")) || IsTrue(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));

    /// <summary>Why the container tests are skipped here, as xunit's skip reason, or null when they run.</summary>
    public static string? Skip =>
        InCi || DockerReachable() ? null : "No Docker is reachable here. CI always runs these tests, and fails without Docker.";

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
    // container would then fail every test instead of skipping them.
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

/// <summary>Marks a test class whose <see cref="JobsFactAttribute"/> tests need Docker for their database.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class RequiresDockerAttribute : Attribute
{
}

/// <summary>A fact that is skipped, outside CI, when its class carries <see cref="RequiresDockerAttribute"/> and Docker is not here.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
[XunitTestCaseDiscoverer("SkathIO.Rogue.Consumer.Jobs.Tests.JobsFactDiscoverer", "SkathIO.Rogue.Consumer.Jobs.Tests")]
public sealed class JobsFactAttribute : FactAttribute
{
}

/// <summary>Discovers a <see cref="JobsFactAttribute"/> test, skipped when its class needs Docker that is not here.</summary>
public sealed class JobsFactDiscoverer : IXunitTestCaseDiscoverer
{
    private readonly IMessageSink _diagnostics;
    private readonly FactDiscoverer _facts;

    /// <summary>Called by xunit.</summary>
    public JobsFactDiscoverer(IMessageSink diagnosticMessageSink)
    {
        _diagnostics = diagnosticMessageSink;
        _facts = new FactDiscoverer(diagnosticMessageSink);
    }

    /// <inheritdoc />
    public IEnumerable<IXunitTestCase> Discover(ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod, IAttributeInfo factAttribute)
    {
        bool needsDocker = testMethod.TestClass.Class.GetCustomAttributes(typeof(RequiresDockerAttribute)).Any();
        string? skip = needsDocker ? DockerGate.Skip : null;
        if (skip is null)
        {
            return _facts.Discover(discoveryOptions, testMethod, factAttribute);
        }
        return new IXunitTestCase[]
        {
            new XunitSkippedDataRowTestCase(_diagnostics, discoveryOptions.MethodDisplayOrDefault(), discoveryOptions.MethodDisplayOptionsOrDefault(), testMethod, skip),
        };
    }
}
