using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue.Messaging.Outbox;

/// <summary>
/// Publishes messages inside the caller's own database transaction. Each message is stored as a job
/// in the jobs store, in that transaction: if it commits, a job worker relays the message to the
/// <see cref="IMessageTransport"/>; if it rolls back, nothing is stored and nothing is sent.
/// </summary>
/// <remarks>
/// A relay job can run more than once (its worker can stop after sending and before recording that),
/// so the same message, with the same id, can reach the transport twice; an inbox on the consuming
/// side runs its handlers once. Messages are relayed one job each, in no guaranteed order.
/// </remarks>
public interface IMessageOutbox
{
    /// <summary>
    /// Stores <paramref name="message"/> inside <paramref name="transaction"/>, in the envelope
    /// <see cref="IMessageBus.Publish{TMessage}"/> would send, and returns the message's id. Nothing
    /// is sent before the transaction commits, and nothing at all if it rolls back.
    /// </summary>
    /// <param name="message">The message, of a type marked <see cref="MessageAttribute"/>.</param>
    /// <param name="transaction">
    /// The caller's open transaction on the jobs store's database: an <c>NpgsqlTransaction</c> for the
    /// Postgres store, a <c>SqlTransaction</c> for the SQL Server store. With EF Core, pass
    /// <c>Database.CurrentTransaction.GetDbTransaction()</c>.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="InvalidOperationException">
    /// The message's type is not a message, its body is over <see cref="MessagingOptions.MaxMessageBytes"/>,
    /// the registered job store cannot join a database transaction, or the transaction has completed.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The transaction is not one of the job store's database client, or the stored message is over
    /// <c>JobsOptions.MaxPayloadBytes</c>.
    /// </exception>
    ValueTask<string> Publish<TMessage>(TMessage message, DbTransaction transaction, CancellationToken cancellationToken = default)
        where TMessage : notnull;
}
