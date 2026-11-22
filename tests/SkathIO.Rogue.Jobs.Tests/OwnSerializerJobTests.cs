using System;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue.Jobs.StoreContract;
using Xunit;

namespace SkathIO.Rogue.Jobs.Tests;

/// <summary>
/// A job a package ships for its own use: it carries its own serializer, so the application's
/// serializer context need not cover it, and it runs without a [Job] attribute.
/// </summary>
public sealed class OwnSerializerJobTests
{
    [Fact]
    public async Task AJobWithItsOwnSerializer_IsEnqueuedAndRun_ThoughTheContextDoesNotCoverIt()
    {
        var ran = new ConcurrentQueue<string>();
        await using JobsHost host = JobsHost.Create(services: s => s.AddJobDefinition(Definition(ran)));

        NewJob job = host.Scheduler.CreateJob(new PackageWork("relay me"));
        await host.Store.EnqueueAsync(new[] { job }, CancellationToken.None);
        int count = await host.Worker.RunOnceAsync();

        Assert.Equal(1, count);
        Assert.Equal(new[] { "relay me" }, ran);
        Assert.Equal("package.work", job.Kind);
        Assert.Equal(JobState.Succeeded, (await host.SnapshotAsync(job.Id)).State);
    }

    [Fact]
    public void AJobWithItsOwnSerializer_MustHaveDefaults_AndATypeInfo()
    {
        Assert.Throws<ArgumentException>(() => new JobDefinition<PackageWork>("package.work", JobDefaults.None, PackageJson.Default.PackageWork, static (_, _, _) => default));
        Assert.Throws<ArgumentNullException>(() => new JobDefinition<PackageWork>("package.work", new JobDefaults(null, 0, 0, 0), null!, static (_, _, _) => default));
        Assert.Throws<ArgumentNullException>(() => new JobDefinition<PackageWork>("package.work", new JobDefaults(null, 0, 0, 0), PackageJson.Default.PackageWork, null!));
    }

    [Fact]
    public async Task ACommandWithoutItsOwnSerializer_IsStillRefused_WhenTheContextDoesNotCoverIt()
    {
        var plain = new JobDefinition<PackageWork>("package.plain", new JobDefaults(null, 0, 0, 0), static (_, _, _) => default);
        await using JobsHost host = JobsHost.Create(services: s => s.AddJobDefinition(plain));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => host.Scheduler);

        Assert.Contains(typeof(PackageWork).ToString(), error.Message, StringComparison.Ordinal);
    }

    private static JobDefinition Definition(ConcurrentQueue<string> ran) => new JobDefinition<PackageWork>(
        "package.work",
        new JobDefaults(null, 0, 0, 0),
        PackageJson.Default.PackageWork,
        (_, work, _) =>
        {
            ran.Enqueue(work.Text);
            return default;
        });
}

/// <summary>Not a Rogue command: only a job definition of its own makes it runnable.</summary>
public sealed record PackageWork(string Text);

[JsonSerializable(typeof(PackageWork))]
internal sealed partial class PackageJson : JsonSerializerContext
{
}

/// <summary>The in-memory store against the shared inbox contract.</summary>
public sealed class InMemoryMessageInboxStoreContractTests : MessageInboxStoreContract
{
    protected override Task<IMessageInboxStore> CreateInboxAsync(TimeProvider clock) => Task.FromResult<IMessageInboxStore>(new InMemoryJobStore(clock));
}
