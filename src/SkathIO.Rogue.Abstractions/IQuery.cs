namespace SkathIO.Rogue;

/// <summary>Semantic marker for a query that returns <typeparamref name="TResponse"/>.</summary>
public interface IQuery<out TResponse> : IRequest<TResponse> { }
