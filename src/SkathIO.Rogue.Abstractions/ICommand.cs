namespace SkathIO.Rogue;

/// <summary>
/// Primary CQS marker for a command that returns <typeparamref name="TResponse"/>. A command
/// expresses an intent to change state. Independent contract — derives from no shared request
/// marker, so a type is either a command or a query, never both.
/// </summary>
public interface ICommand<out TResponse> { }

/// <summary>
/// Primary CQS marker for a command that produces no return value (modelled as <see cref="Unit"/>).
/// Independent contract — derives only from <see cref="ICommand{TResponse}"/> for response typing,
/// not from any shared request marker, so it can never also be a query.
/// </summary>
public interface ICommand : ICommand<Unit> { }
