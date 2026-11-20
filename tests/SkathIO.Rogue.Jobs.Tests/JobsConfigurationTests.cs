using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace SkathIO.Rogue.Jobs.Tests;

/// <summary>Options, definitions and the registry's startup checks, each at and just past its limits.</summary>
public sealed class JobsConfigurationTests
{
    [Theory]
    [InlineData("BatchSize", "0")]
    [InlineData("BatchSize", "10001")]
    [InlineData("MaxConcurrency", "0")]
    [InlineData("DefaultMaxAttempts", "0")]
    [InlineData("DefaultBackoff", "-1")]
    [InlineData("MaxBackoff", "99999999")]
    [InlineData("BackoffJitter", "-0.01")]
    [InlineData("BackoffJitter", "1.01")]
    [InlineData("BackoffJitter", "NaN")]
    [InlineData("PollInterval", "0")]
    [InlineData("LeaseDuration", "0")]
    [InlineData("MaxPayloadBytes", "0")]
    public void Option_JustPastItsLimit_IsRejected(string option, string value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddRogueJobs(o => Apply(o, option, value)));
    }

    [Theory]
    [InlineData("BatchSize", "1")]
    [InlineData("BatchSize", "10000")]
    [InlineData("MaxConcurrency", "1")]
    [InlineData("DefaultMaxAttempts", "1")]
    [InlineData("DefaultBackoff", "0")]
    [InlineData("MaxBackoff", "100000000")]
    [InlineData("BackoffJitter", "0")]
    [InlineData("BackoffJitter", "1")]
    [InlineData("PollInterval", "1")]
    [InlineData("LeaseDuration", "1")]
    [InlineData("MaxPayloadBytes", "1")]
    public void Option_AtItsLimit_IsAccepted(string option, string value)
    {
        new ServiceCollection().AddRogueJobs(o => Apply(o, option, value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("tab\t")]
    public void QueueName_ThatIsEmptyOrHasWhiteSpace_IsRejected(string queue)
    {
        Assert.ThrowsAny<ArgumentException>(() => new ServiceCollection().AddRogueJobs(o => o.DefaultQueue = queue));
        Assert.ThrowsAny<ArgumentException>(() => new ServiceCollection().AddRogueJobs(o => o.Queues.Add(queue)));
    }

    [Fact]
    public void QueueName_AtTheLengthLimit_IsAccepted_AndOnePast_IsRejected()
    {
        new ServiceCollection().AddRogueJobs(o => o.DefaultQueue = new string('q', JobLimits.MaxQueueLength));

        Assert.ThrowsAny<ArgumentException>(() => new ServiceCollection().AddRogueJobs(o => o.DefaultQueue = new string('q', JobLimits.MaxQueueLength + 1)));
        Assert.ThrowsAny<ArgumentException>(() => new ServiceCollection().AddRogueJobs(o => o.DefaultQueue = null!));
    }

    [Fact]
    public async Task MissingSerializerContext_FailsAtStartup()
    {
        await using JobsHost host = JobsHost.Create(static o => o.SerializerContext = null);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => host.Scheduler);
        Assert.Contains("SerializerContext", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SerializerContext_ThatMissesJobs_FailsAtStartup_NamingThem()
    {
        await using JobsHost host = JobsHost.Create(static o => o.SerializerContext = PartialJobsJson.Default);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => host.Worker);
        Assert.Contains(typeof(FlakyJob).ToString(), error.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(PriceJob).ToString(), error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(typeof(RecordValue).ToString(), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnmarkedCommand_IsLeftOut_EvenWhenTheContextCoversIt_AndEnqueueingItNamesTheAttribute()
    {
        var definitions = new JobDefinition[]
        {
            new JobDefinition<RecordValue>("marked", new JobDefaults(null, 0, 0, 0), static (_, _, _) => default),
            new JobDefinition<UnmarkedJob>("unmarked", JobDefaults.None, static (_, _, _) => default),
        };
        var registry = new JobRegistry(definitions, new JobsOptions { SerializerContext = TestJobsJson.Default });
        var scheduler = new JobScheduler(registry, new InMemoryJobStore(), new JobSignal());

        Assert.True(registry.TryGet("marked", out _));
        Assert.False(registry.TryGet("unmarked", out _));
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await scheduler.Enqueue(new UnmarkedJob(1)));
        Assert.Contains("[Job]", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnmarkedCommands_WithNoSerializerContext_DoNotFailStartup()
    {
        var definitions = new JobDefinition[] { new JobDefinition<UnmarkedJob>("unmarked", JobDefaults.None, static (_, _, _) => default) };

        var registry = new JobRegistry(definitions, new JobsOptions());

        Assert.False(registry.TryGet("unmarked", out _));
    }

    [Fact]
    public void MarkedCommand_WithNoSerializerContext_StillFailsStartup()
    {
        var definitions = new JobDefinition[] { new JobDefinition<RecordValue>("marked", new JobDefaults(null, 0, 0, 0), static (_, _, _) => default) };

        Assert.Throws<InvalidOperationException>(() => new JobRegistry(definitions, new JobsOptions()));
    }

    [Fact]
    public void TwoCommands_UnderOneKind_FailAtStartup()
    {
        var definitions = new JobDefinition[]
        {
            new JobDefinition<RecordValue>("same.kind", new JobDefaults(null, 0, 0, 0), static (_, _, _) => default),
            new JobDefinition<UnmarkedJob>("same.kind", new JobDefaults(null, 0, 0, 0), static (_, _, _) => default),
        };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => new JobRegistry(definitions, new JobsOptions { SerializerContext = TestJobsJson.Default }));
        Assert.Contains("same.kind", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SameDefinition_RegisteredTwice_IsKeptOnce()
    {
        var services = new ServiceCollection();
        var definition = new JobDefinition<RecordValue>("once", JobDefaults.None, static (_, _, _) => default);

        services.AddJobDefinition(definition);
        services.AddJobDefinition(new JobDefinition<RecordValue>("once", JobDefaults.None, static (_, _, _) => default));

        Assert.Single(services, static d => d.ServiceType == typeof(JobDefinition));
    }

    [Fact]
    public void AddRogueJobWorker_HostsTheWorkerOnce_HoweverOftenItIsCalled()
    {
        var services = new ServiceCollection();
        services.AddRogueJobs();

        services.AddRogueJobWorker();
        services.AddRogueJobWorker();

        using ServiceProvider provider = services.BuildServiceProvider();
        IHostedService[] hosted = provider.GetServices<IHostedService>().ToArray();
        Assert.Equal(2, hosted.Length);
        Assert.Single(hosted, static h => h is JobWorkerService);
        Assert.Single(hosted, static h => h is RecurringJobService);
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddRogueJobWorker());
    }

    [Fact]
    public void RegistryQueues_DefaultToTheDefaultQueuePlusEveryJobQueue()
    {
        using ServiceProvider services = JobsHost.Create().Services;

        JobRegistry registry = services.GetRequiredService<JobRegistry>();

        Assert.Equal(new[] { "billing", "default" }, registry.Queues.OrderBy(static q => q, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("with space")]
    public void JobKind_ThatIsEmptyOrHasWhiteSpace_IsRejected(string kind)
    {
        Assert.ThrowsAny<ArgumentException>(() => new JobDefinition<RecordValue>(kind, JobDefaults.None, static (_, _, _) => default));
    }

    [Fact]
    public void JobKind_AtTheLengthLimit_IsAccepted_AndOnePast_IsRejected()
    {
        _ = new JobDefinition<RecordValue>(new string('k', JobLimits.MaxKindLength), JobDefaults.None, static (_, _, _) => default);

        Assert.ThrowsAny<ArgumentException>(() => new JobDefinition<RecordValue>(new string('k', JobLimits.MaxKindLength + 1), JobDefaults.None, static (_, _, _) => default));
        Assert.Throws<ArgumentNullException>(() => new JobDefinition<RecordValue>("k", null!, static (_, _, _) => default));
        Assert.Throws<ArgumentNullException>(() => new JobDefinition<RecordValue>("k", JobDefaults.None, null!));
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 0, -1)]
    public void JobDefaults_RejectANegativeValue(int maxAttempts, int backoffSeconds, int timeoutSeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new JobDefaults(null, maxAttempts, backoffSeconds, timeoutSeconds));
    }

    [Fact]
    public void JobDefaults_AcceptZero_AndRejectAnEmptyQueue()
    {
        var defaults = new JobDefaults(null, 0, 0, 0);

        Assert.Null(defaults.Queue);
        Assert.ThrowsAny<ArgumentException>(() => new JobDefaults(string.Empty, 0, 0, 0));
    }

    [Fact]
    public void NewJob_RejectsAnEmptyIdOrNoAttempts_AndAcceptsOneAttempt()
    {
        Assert.Throws<ArgumentException>(() => new NewJob { Id = Guid.Empty, Kind = "k", Queue = "q", Payload = default, MaxAttempts = 1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new NewJob { Id = Guid.NewGuid(), Kind = "k", Queue = "q", Payload = default, MaxAttempts = 0 });
        Assert.Equal(1, new NewJob { Id = Guid.NewGuid(), Kind = "k", Queue = "q", Payload = default, MaxAttempts = 1 }.MaxAttempts);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData(null)]
    public void NewJob_RejectsABadKindOrQueue(string? name)
    {
        Assert.ThrowsAny<ArgumentException>(() => new NewJob { Id = Guid.NewGuid(), Kind = name!, Queue = "q", Payload = default, MaxAttempts = 1 });
        Assert.ThrowsAny<ArgumentException>(() => new NewJob { Id = Guid.NewGuid(), Kind = "k", Queue = name!, Payload = default, MaxAttempts = 1 });
    }

    [Fact]
    public async Task IgnoreResult_CompletesWithTheSend_AndPassesOnItsFailure()
    {
        await JobDefinition.IgnoreResult(new ValueTask<int>(5));
        await JobDefinition.IgnoreResult(new ValueTask<int>(Task.Run(static async () =>
        {
            await Task.Yield();
            return 5;
        })));

        await Assert.ThrowsAsync<FormatException>(async () => await JobDefinition.IgnoreResult(new ValueTask<int>(Task.FromException<int>(new FormatException()))));
    }

    private static void Apply(JobsOptions options, string option, string value)
    {
        switch (option)
        {
            case "BatchSize": options.BatchSize = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
            case "MaxConcurrency": options.MaxConcurrency = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
            case "DefaultMaxAttempts": options.DefaultMaxAttempts = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
            case "MaxPayloadBytes": options.MaxPayloadBytes = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
            case "BackoffJitter": options.BackoffJitter = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
            // Durations are in ticks; MaxBackoff is checked against the default backoff of ten seconds.
            case "DefaultBackoff": options.DefaultBackoff = TimeSpan.FromTicks(long.Parse(value, System.Globalization.CultureInfo.InvariantCulture)); break;
            case "MaxBackoff": options.MaxBackoff = TimeSpan.FromTicks(long.Parse(value, System.Globalization.CultureInfo.InvariantCulture)); break;
            case "PollInterval": options.PollInterval = TimeSpan.FromTicks(long.Parse(value, System.Globalization.CultureInfo.InvariantCulture)); break;
            case "LeaseDuration": options.LeaseDuration = TimeSpan.FromTicks(long.Parse(value, System.Globalization.CultureInfo.InvariantCulture)); break;
            default: throw new ArgumentException("Unknown option " + option, nameof(option));
        }
    }
}
