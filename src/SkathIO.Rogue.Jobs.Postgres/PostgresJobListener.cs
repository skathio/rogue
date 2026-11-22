using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace SkathIO.Rogue.Jobs.Postgres;

/// <summary>
/// Holds one connection that LISTENs on the store's channel and wakes this process's worker on each
/// NOTIFY, so a job enqueued by any process starts without waiting out the poll interval. When the
/// connection fails it reconnects after a delay; meanwhile the worker's polling still finds every job.
/// </summary>
internal sealed class PostgresJobListener : BackgroundService
{
    private static readonly Action<ILogger, Exception?> _logListenFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(101, "JobListenFailed"),
            "Listening for job notifications failed; workers fall back to polling until the listener reconnects.");

    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresSql _sql;
    private readonly TimeSpan _reconnectDelay;
    private readonly Action _wake;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public PostgresJobListener(PostgresJobStore store, Action wake, TimeProvider time, ILogger logger)
    {
        _dataSource = store.DataSource;
        _sql = store.Sql;
        _reconnectDelay = store.Options.ListenReconnectDelay;
        _wake = wake;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logListenFailed(_logger, ex);
                try
                {
                    await Task.Delay(_reconnectDelay, _time, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task ListenAsync(CancellationToken stoppingToken)
    {
        NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(stoppingToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            connection.Notification += (_, _) => _wake();
            await using (var listen = new NpgsqlCommand(_sql.Listen, connection))
            {
                await listen.ExecuteNonQueryAsync(stoppingToken).ConfigureAwait(false);
            }

            // Jobs enqueued while no connection listened sent their NOTIFY to nobody: look once now.
            _wake();
            while (true)
            {
                await connection.WaitAsync(stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
