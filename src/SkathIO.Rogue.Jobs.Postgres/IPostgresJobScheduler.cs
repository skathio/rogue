using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace SkathIO.Rogue.Jobs.Postgres;

/// <summary>
/// Enqueues commands as jobs on the caller's own Postgres connection or transaction, so a job
/// commits or rolls back with the data it was enqueued for, and wakes workers only once committed.
/// The connection's database must be the one the job store uses.
/// </summary>
public interface IPostgresJobScheduler
{
    /// <summary>Enqueues one command inside <paramref name="transaction"/> and returns its job id.</summary>
    /// <exception cref="InvalidOperationException"><typeparamref name="TCommand"/> is not a job, or the transaction has completed.</exception>
    /// <exception cref="ArgumentException">The serialized command is larger than <see cref="JobsOptions.MaxPayloadBytes"/>.</exception>
    ValueTask<Guid> Enqueue<TCommand>(TCommand command, NpgsqlTransaction transaction, CancellationToken cancellationToken = default)
        where TCommand : notnull;

    /// <summary>Enqueues one command on the open <paramref name="connection"/>, in its current transaction if any, and returns its job id.</summary>
    /// <exception cref="InvalidOperationException"><typeparamref name="TCommand"/> is not a job, or the connection is not open.</exception>
    /// <exception cref="ArgumentException">The serialized command is larger than <see cref="JobsOptions.MaxPayloadBytes"/>.</exception>
    ValueTask<Guid> Enqueue<TCommand>(TCommand command, NpgsqlConnection connection, CancellationToken cancellationToken = default)
        where TCommand : notnull;

    /// <summary>Enqueues every command inside <paramref name="transaction"/> with one binary COPY, and returns their job ids in order.</summary>
    /// <exception cref="InvalidOperationException"><typeparamref name="TCommand"/> is not a job, or the transaction has completed.</exception>
    /// <exception cref="ArgumentException">A command is null, or a serialized command is larger than <see cref="JobsOptions.MaxPayloadBytes"/>.</exception>
    ValueTask<IReadOnlyList<Guid>> EnqueueMany<TCommand>(IReadOnlyList<TCommand> commands, NpgsqlTransaction transaction, CancellationToken cancellationToken = default)
        where TCommand : notnull;

    /// <summary>Enqueues every command on the open <paramref name="connection"/>, in its current transaction if any, and returns their job ids in order.</summary>
    /// <exception cref="InvalidOperationException"><typeparamref name="TCommand"/> is not a job, or the connection is not open.</exception>
    /// <exception cref="ArgumentException">A command is null, or a serialized command is larger than <see cref="JobsOptions.MaxPayloadBytes"/>.</exception>
    ValueTask<IReadOnlyList<Guid>> EnqueueMany<TCommand>(IReadOnlyList<TCommand> commands, NpgsqlConnection connection, CancellationToken cancellationToken = default)
        where TCommand : notnull;
}
