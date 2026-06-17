namespace SkathIO.Rogue;

/// <summary>
/// Primary CQS marker for a query that returns <typeparamref name="TResponse"/>. A query reads
/// state and must not mutate it. Independent contract — derives from no shared request marker
/// (PD-40 clean break).
/// </summary>
public interface IQuery<out TResponse> { }
