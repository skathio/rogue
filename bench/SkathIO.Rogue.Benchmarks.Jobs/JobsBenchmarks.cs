using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue.Jobs;

namespace SkathIO.Rogue.JobsBenchmarks;

/// <summary>
/// Enqueues a number of small jobs and waits until every one has run, on Rogue and on Hangfire, each
/// with its storage and worker started once per benchmark and left at their defaults. Hangfire has no
/// bulk enqueue outside Hangfire Pro, so it enqueues one job at a time; Rogue is measured both ways,
/// and with one service scope per claimed batch instead of one per job.
/// </summary>
public abstract class JobsBenchmarks
{
    private const string HangfireTarget = nameof(Hangfire_EnqueueEach_AndRun);

    private SmallJob[] _commands = Array.Empty<SmallJob>();
    private BackgroundJobServer? _hangfireServer;
    private BackgroundJobClient? _hangfireClient;
    private ServiceProvider? _rogueServices;
    private IJobScheduler? _rogueScheduler;
    private CancellationTokenSource? _rogueStopping;
    private Task _rogueWorker = Task.CompletedTask;

    /// <summary>How many jobs one operation enqueues and runs; each concrete benchmark sets its own values with <c>[Params]</c>.</summary>
    public virtual int Jobs { get; set; }

    /// <summary>Hangfire's storage for this benchmark.</summary>
    protected abstract JobStorage CreateHangfireStorage();

    /// <summary>Registers Rogue's job store for this benchmark; registering none keeps the in-memory store.</summary>
    protected abstract void AddRogueStore(IServiceCollection services);

    /// <summary>Starts Hangfire's server on a fresh storage.</summary>
    [GlobalSetup(Target = HangfireTarget)]
    public void StartHangfire()
    {
        GlobalConfiguration.Configuration
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings();
        JobStorage storage = CreateHangfireStorage();
        _hangfireClient = new BackgroundJobClient(storage);
        _hangfireServer = new BackgroundJobServer(new BackgroundJobServerOptions(), storage);
    }

    /// <summary>Stops Hangfire's server.</summary>
    [GlobalCleanup(Target = HangfireTarget)]
    public void StopHangfire() => _hangfireServer?.Dispose();

    /// <summary>Starts Rogue's worker with a scope per job.</summary>
    [GlobalSetup(Targets = new[] { nameof(Rogue_EnqueueEach_AndRun), nameof(Rogue_EnqueueMany_AndRun) })]
    public void StartRogue() => StartRogue(scopePerBatch: false);

    /// <summary>Starts Rogue's worker with a scope per claimed batch.</summary>
    [GlobalSetup(Target = nameof(Rogue_EnqueueMany_ScopePerBatch_AndRun))]
    public void StartRogueWithBatchScopes() => StartRogue(scopePerBatch: true);

    /// <summary>Stops Rogue's worker and disposes its services.</summary>
    [GlobalCleanup(Targets = new[] { nameof(Rogue_EnqueueEach_AndRun), nameof(Rogue_EnqueueMany_AndRun), nameof(Rogue_EnqueueMany_ScopePerBatch_AndRun) })]
    public async Task StopRogue()
    {
        if (_rogueStopping is not null)
        {
            await _rogueStopping.CancelAsync();
            await _rogueWorker;
            _rogueStopping.Dispose();
        }
        if (_rogueServices is not null)
        {
            await _rogueServices.DisposeAsync();
        }
    }

    /// <summary>Hangfire, one <c>Enqueue</c> call per job: the baseline.</summary>
    [Benchmark(Baseline = true)]
    public Task Hangfire_EnqueueEach_AndRun()
    {
        Task done = JobCounter.Expect(Jobs);
        for (int i = 0; i < Jobs; i++)
        {
            int value = i;
            _hangfireClient!.Enqueue(() => HangfireSmallJob.Run(value));
        }
        return done;
    }

    /// <summary>Rogue, one <c>Enqueue</c> call per job.</summary>
    [Benchmark]
    public async Task Rogue_EnqueueEach_AndRun()
    {
        Task done = JobCounter.Expect(Jobs);
        for (int i = 0; i < _commands.Length; i++)
        {
            await _rogueScheduler!.Enqueue(_commands[i]);
        }
        await done;
    }

    /// <summary>Rogue, every job in one <c>EnqueueMany</c> call.</summary>
    [Benchmark]
    public Task Rogue_EnqueueMany_AndRun() => EnqueueManyAndRunAsync();

    /// <summary>Rogue, every job in one <c>EnqueueMany</c> call, with one service scope per claimed batch.</summary>
    [Benchmark]
    public Task Rogue_EnqueueMany_ScopePerBatch_AndRun() => EnqueueManyAndRunAsync();

    private async Task EnqueueManyAndRunAsync()
    {
        Task done = JobCounter.Expect(Jobs);
        await _rogueScheduler!.EnqueueMany<SmallJob>(_commands);
        await done;
    }

    private void StartRogue(bool scopePerBatch)
    {
        var commands = new SmallJob[Jobs];
        for (int i = 0; i < commands.Length; i++)
        {
            commands[i] = new SmallJob(i);
        }
        _commands = commands;

        var services = new ServiceCollection();
        services.AddRogue();
        services.AddRogueJobs(options =>
        {
            options.SerializerContext = BenchJobsJson.Default;
            options.ScopePerBatch = scopePerBatch;
        });
        AddRogueStore(services);
        _rogueServices = services.BuildServiceProvider();
        _rogueScheduler = _rogueServices.GetRequiredService<IJobScheduler>();
        _rogueStopping = new CancellationTokenSource();
        _rogueWorker = _rogueServices.GetRequiredService<JobWorker>().RunAsync(_rogueStopping.Token);
    }
}
