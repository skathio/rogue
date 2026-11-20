using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue.Jobs;

/// <summary>
/// Enqueues commands as background jobs. A job runs the command's handler, through the full Rogue
/// pipeline, on a worker, in a service scope of its own.
/// </summary>
public interface IJobScheduler
{
    /// <summary>Enqueues one command and returns its job id.</summary>
    /// <exception cref="InvalidOperationException"><typeparamref name="TCommand"/> is not a job.</exception>
    /// <exception cref="ArgumentException">The serialized command is larger than <see cref="JobsOptions.MaxPayloadBytes"/>.</exception>
    ValueTask<Guid> Enqueue<TCommand>(TCommand command, CancellationToken cancellationToken = default)
        where TCommand : notnull;

    /// <summary>Enqueues every command in one store call, all or none, and returns their job ids in order.</summary>
    /// <exception cref="InvalidOperationException"><typeparamref name="TCommand"/> is not a job.</exception>
    /// <exception cref="ArgumentException">A serialized command is larger than <see cref="JobsOptions.MaxPayloadBytes"/>.</exception>
    ValueTask<IReadOnlyList<Guid>> EnqueueMany<TCommand>(IReadOnlyList<TCommand> commands, CancellationToken cancellationToken = default)
        where TCommand : notnull;

    /// <summary>
    /// Serializes <paramref name="command"/> into a job ready to store, without storing it: for a
    /// store's own enqueue, such as one that joins the caller's database transaction.
    /// </summary>
    /// <exception cref="InvalidOperationException"><typeparamref name="TCommand"/> is not a job.</exception>
    /// <exception cref="ArgumentException">The serialized command is larger than <see cref="JobsOptions.MaxPayloadBytes"/>.</exception>
    NewJob CreateJob<TCommand>(TCommand command)
        where TCommand : notnull;
}
