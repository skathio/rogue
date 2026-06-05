namespace SkathIO.Rogue;

/// <summary>Handles a query of type <typeparamref name="TQuery"/> returning <typeparamref name="TResponse"/>.</summary>
public interface IQueryHandler<in TQuery, TResponse> : IRequestHandler<TQuery, TResponse>
    where TQuery : IQuery<TResponse> { }
