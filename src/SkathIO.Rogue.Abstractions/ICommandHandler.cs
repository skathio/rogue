using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue;

/// <summary>
/// Handles a command of type <typeparamref name="TCommand"/> returning
/// <typeparamref name="TResponse"/>. Primary CQS handler contract — declares its own
/// <see cref="Handle"/> with no shared request-handler base (PD-40 clean break).
/// </summary>
public interface ICommandHandler<in TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    /// <summary>Handles the command.</summary>
    ValueTask<TResponse> Handle(TCommand command, CancellationToken cancellationToken);
}

/// <summary>
/// Handles a command of type <typeparamref name="TCommand"/> that produces no return value.
/// Primary CQS handler contract — declares its own <see cref="Handle"/> with no shared
/// request-handler base (PD-40 clean break).
/// </summary>
public interface ICommandHandler<in TCommand>
    where TCommand : ICommand
{
    /// <summary>Handles the command.</summary>
#if NETSTANDARD2_0
    ValueTask<Unit> Handle(TCommand command, CancellationToken cancellationToken);
#else
    ValueTask Handle(TCommand command, CancellationToken cancellationToken);
#endif
}
