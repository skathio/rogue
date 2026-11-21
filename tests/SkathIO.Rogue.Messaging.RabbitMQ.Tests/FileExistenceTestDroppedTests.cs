using System;
using System.Linq;
using System.Reflection;
using Xunit;

namespace SkathIO.Rogue.Messaging.RabbitMQ.Tests;

/// <summary>
/// The test that only checked the wait-queue files were in the source tree said nothing about how
/// the transport behaves, so the maintainer had it dropped. This checks the compiled test assembly,
/// not the file system, so it holds in any copy of the source.
/// </summary>
public sealed class FileExistenceTestDroppedTests
{
    [Fact]
    public void TheTestAssembly_HasNoCommittedWaitQueueWorkTests()
    {
        Assembly tests = typeof(FileExistenceTestDroppedTests).Assembly;

        Type? dropped = tests.GetType("SkathIO.Rogue.Messaging.RabbitMQ.Tests.CommittedWaitQueueWorkTests", throwOnError: false);

        Assert.Null(dropped);
        Assert.DoesNotContain(tests.GetTypes().Select(type => type.Name), name => name == "CommittedWaitQueueWorkTests");
    }
}
