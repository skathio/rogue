namespace SkathIO.Rogue;

/// <summary>Marker for a request that returns <typeparamref name="TResponse"/>.</summary>
public interface IRequest<out TResponse> : IBaseRequest { }

/// <summary>Marker for a request that returns no value (returns <see cref="Unit"/>).</summary>
public interface IRequest : IRequest<Unit> { }

/// <summary>Base marker interface for all request types.</summary>
public interface IBaseRequest { }
