using System;
using System.IO;
using Xunit;

namespace SkathIO.Rogue.Messaging.RabbitMQ.Tests;

/// <summary>
/// An earlier run left the wait queue's TTL ceiling, its docs and its two test files uncommitted, and
/// the maintainer asked for that work to be kept. Whether a file is in a git commit is a fact about
/// the repository, not the code. A test of it fails in a source tarball or a <c>git archive</c> copy,
/// so this checks what holds in every copy of the source instead: each file is in the source tree.
/// </summary>
public sealed class CommittedWaitQueueWorkTests
{
    public static TheoryData<string> WaitQueueWork => new TheoryData<string>
    {
        "src/SkathIO.Rogue.Messaging.RabbitMQ/RabbitMqTransport.Topology.cs",
        "src/SkathIO.Rogue.Messaging.RabbitMQ/RabbitMqTransport.cs",
        "src/SkathIO.Rogue.Messaging.RabbitMQ/RabbitMqTransportOptions.cs",
        "CHANGELOG.md",
        "docs/messaging-rabbitmq.md",
        "tests/SkathIO.Rogue.Messaging.RabbitMQ.Tests/WaitQueueCeilingTests.cs",
        "tests/SkathIO.Rogue.Messaging.RabbitMQ.Tests/WaitQueueDeferralEdgeTests.cs",
    };

    [Theory]
    [MemberData(nameof(WaitQueueWork))]
    public void TheWaitQueueWork_IsPartOfTheSourceTree(string path)
    {
        string full = Path.Combine(RepoRoot(), path.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(File.Exists(full), $"{path} is not in the source tree.");
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "SkathIO.Rogue.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("SkathIO.Rogue.sln was not found above " + AppContext.BaseDirectory);
    }
}
