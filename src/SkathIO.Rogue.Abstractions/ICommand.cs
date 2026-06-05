namespace SkathIO.Rogue;

/// <summary>Semantic marker for a command that returns <typeparamref name="TResponse"/>.</summary>
public interface ICommand<out TResponse> : IRequest<TResponse> { }

/// <summary>Semantic marker for a command that produces no return value.</summary>
public interface ICommand : ICommand<Unit>, IRequest { }
