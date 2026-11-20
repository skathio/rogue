using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace SkathIO.Rogue.Jobs;

/// <summary>
/// Runs the <see cref="JobWorker"/> for the life of the host: <c>AddRogueJobs</c> registers it, so a
/// generic or web host starts the worker with no further code and stops it on shutdown.
/// </summary>
internal sealed class JobWorkerService : BackgroundService
{
    private readonly JobWorker _worker;

    public JobWorkerService(JobWorker worker) => _worker = worker;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _worker.RunAsync(stoppingToken);
}
