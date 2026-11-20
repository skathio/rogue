using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace SkathIO.Rogue.Jobs.StoreContract;

/// <summary>
/// A fact that a store's test class can make depend on a database container: when the class carries
/// <see cref="RequiresDockerAttribute"/>, the test is skipped on a machine with no reachable Docker,
/// unless it runs in CI, where a missing Docker must fail the run rather than pass it silently.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
[XunitTestCaseDiscoverer("SkathIO.Rogue.Jobs.StoreContract.StoreFactDiscoverer", "SkathIO.Rogue.Jobs.StoreContract")]
public sealed class StoreFactAttribute : FactAttribute
{
}

/// <summary>Marks a test class whose <see cref="StoreFactAttribute"/> tests need Docker for their database.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class RequiresDockerAttribute : Attribute
{
}

/// <summary>Where the store integration tests find their database.</summary>
public static class TestDatabase
{
    /// <summary>
    /// The environment variable that names a Postgres database to test against instead of a container,
    /// for a machine without Docker. The tests create schemas of their own in it.
    /// </summary>
    public const string PostgresVariable = "ROGUE_TEST_POSTGRES";

    /// <summary>The connection string from <see cref="PostgresVariable"/>, or null to use a container.</summary>
    public static string? PostgresConnectionString =>
        Environment.GetEnvironmentVariable(PostgresVariable) is { Length: > 0 } value ? value : null;

    /// <summary>True in a CI run, where tests that need Docker must run.</summary>
    public static bool InCi =>
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Why a test that needs a database is skipped here, or null when it can run.</summary>
    public static string? SkipReason()
    {
        if (PostgresConnectionString is not null || InCi || DockerReachable())
        {
            return null;
        }
        return "No Docker is reachable, and " + PostgresVariable + " names no database. CI always runs these tests.";
    }

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
        return sockets.Any(static s => s.Length > 0 && File.Exists(s));
    }
}

/// <summary>Discovers a <see cref="StoreFactAttribute"/> test, skipped when its class needs Docker that is not here.</summary>
public sealed class StoreFactDiscoverer : IXunitTestCaseDiscoverer
{
    private readonly IMessageSink _diagnostics;
    private readonly FactDiscoverer _facts;

    /// <summary>Called by xunit.</summary>
    public StoreFactDiscoverer(IMessageSink diagnosticMessageSink)
    {
        _diagnostics = diagnosticMessageSink;
        _facts = new FactDiscoverer(diagnosticMessageSink);
    }

    /// <inheritdoc />
    public IEnumerable<IXunitTestCase> Discover(ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod, IAttributeInfo factAttribute)
    {
        bool needsDocker = testMethod.TestClass.Class.GetCustomAttributes(typeof(RequiresDockerAttribute)).Any();
        string? skip = needsDocker ? TestDatabase.SkipReason() : null;
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
