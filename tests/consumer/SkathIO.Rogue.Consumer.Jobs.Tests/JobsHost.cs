using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SkathIO.Rogue.Jobs;

namespace SkathIO.Rogue.Consumer.Jobs.Tests;

/// <summary>
/// A service provider wired as an app with jobs wires it, on a clock that moves only when a test moves
/// it, with no retry jitter, so every delay a test checks is exact.
/// </summary>
internal sealed class JobsHost : IAsyncDisposable
{
    /// <summary>When the clock starts: midnight, so the recurring job's first occurrence is half an hour away.</summary>
    public static readonly DateTimeOffset Start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly ServiceProvider _services;
    private readonly List<IHostedService> _started = new List<IHostedService>();

    private JobsHost(ServiceProvider services, ManualTimeProvider clock)
    {
        _services = services;
        Clock = clock;
    }

    public ManualTimeProvider Clock { get; }

    public JobProbe Probe => _services.GetRequiredService<JobProbe>();

    public IJobScheduler Scheduler => _services.GetRequiredService<IJobScheduler>();

    public JobWorker Worker => _services.GetRequiredService<JobWorker>();

    public IJobStore Store => _services.GetRequiredService<IJobStore>();

    /// <summary>The store, watched for recurring occurrences; only in a host created with the worker.</summary>
    public WatchedJobStore Watched => (WatchedJobStore)Store;

    /// <summary>
    /// Builds the host on the store <paramref name="addStore"/> registers. With
    /// <paramref name="hostedWorker"/>, it also registers the hosted worker and recurring scheduler, as a
    /// host that runs jobs does, and watches the store for the occurrences the scheduler stores.
    /// </summary>
    public static JobsHost Create(Action<IServiceCollection> addStore, bool hostedWorker = false)
    {
        var clock = new ManualTimeProvider(Start);
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<JobProbe>();
        addStore(services);
        services.AddRogue();
        services.AddRogueJobs(static options =>
        {
            options.SerializerContext = ConsumerJobsJson.Default;
            options.BackoffJitter = 0;
        });
        if (hostedWorker)
        {
            services.AddRogueJobWorker();
            ServiceDescriptor store = services.Last(static d => d.ServiceType == typeof(IJobStore));
            services.Replace(ServiceDescriptor.Singleton<IJobStore>(sp => new WatchedJobStore(
                (IJobStore)(store.ImplementationInstance ?? store.ImplementationFactory!(sp)))));
        }
        return new JobsHost(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }), clock);
    }

    public async Task<JobSnapshot> SnapshotAsync(Guid id)
    {
        JobSnapshot? snapshot = await Store.GetAsync(id, CancellationToken.None).ConfigureAwait(false);
        return snapshot ?? throw new InvalidOperationException("No job " + id + " is stored.");
    }

    /// <summary>Starts every hosted service, as a host does.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (IHostedService service in _services.GetServices<IHostedService>())
        {
            await service.StartAsync(cancellationToken).ConfigureAwait(false);
            _started.Add(service);
        }
    }

    /// <summary>Stops the hosted services in reverse order, as a host does on shutdown.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        for (int i = _started.Count - 1; i >= 0; i--)
        {
            await _started[i].StopAsync(cancellationToken).ConfigureAwait(false);
        }
        _started.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await _services.DisposeAsync().ConfigureAwait(false);
    }
}
