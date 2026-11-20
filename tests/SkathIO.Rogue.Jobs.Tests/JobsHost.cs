using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue.Jobs.StoreContract;

namespace SkathIO.Rogue.Jobs.Tests;

/// <summary>A service provider wired as an application would wire it, on a manual clock, with no jitter.</summary>
internal sealed class JobsHost : IAsyncDisposable
{
    private JobsHost(ServiceProvider services, ManualTimeProvider clock, JobProbe probe)
    {
        Services = services;
        Clock = clock;
        Probe = probe;
    }

    public ServiceProvider Services { get; }

    public ManualTimeProvider Clock { get; }

    public JobProbe Probe { get; }

    public IJobScheduler Scheduler => Services.GetRequiredService<IJobScheduler>();

    public JobWorker Worker => Services.GetRequiredService<JobWorker>();

    public IJobStore Store => Services.GetRequiredService<IJobStore>();

    public static JobsHost Create(Action<JobsOptions>? configure = null, Action<IServiceCollection>? services = null)
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var probe = new JobProbe();
        var collection = new ServiceCollection();
        collection.AddSingleton<TimeProvider>(clock);
        collection.AddSingleton(probe);
        collection.AddScoped<ScopedCounter>();
        services?.Invoke(collection);
        collection.AddRogue();
        collection.AddRogueJobs(options =>
        {
            options.SerializerContext = TestJobsJson.Default;
            options.BackoffJitter = 0;
            configure?.Invoke(options);
        });
        return new JobsHost(collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }), clock, probe);
    }

    public async Task<JobSnapshot> SnapshotAsync(Guid id) => (await Store.GetAsync(id, CancellationToken.None))!;

    public ValueTask DisposeAsync() => Services.DisposeAsync();
}
