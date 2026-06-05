namespace SkathIO.Rogue;

/// <summary>Handles a command of type <typeparamref name="TCommand"/> returning <typeparamref name="TResponse"/>.</summary>
public interface ICommandHandler<in TCommand, TResponse> : IRequestHandler<TCommand, TResponse>
    where TCommand : ICommand<TResponse> { }

/// <summary>Handles a command of type <typeparamref name="TCommand"/> that produces no return value.</summary>
public interface ICommandHandler<in TCommand> : IRequestHandler<TCommand>
    where TCommand : ICommand { }
