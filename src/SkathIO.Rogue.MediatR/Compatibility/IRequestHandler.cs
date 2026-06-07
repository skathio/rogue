namespace SkathIO.Rogue.Compatibility;

// Shape mirrors MediatR's IRequestHandler<,> but uses ValueTask (Rogue's contract).
// The Phase 6.2 analyzer rewrites Task -> ValueTask in handler bodies.
//
// NOTE: the constraint is `IRequest<TResponse>`, not MediatR's `notnull`. Rogue's
// base IRequestHandler<in TRequest, TResponse> constrains TRequest to IRequest<TResponse>;
// a weaker `notnull` constraint here would not satisfy the base interface (CS0314).
public interface IRequestHandler<TRequest, TResponse> : global::SkathIO.Rogue.IRequestHandler<TRequest, TResponse>
    where TRequest : global::SkathIO.Rogue.IRequest<TResponse> { }

public interface IRequestHandler<TRequest> : global::SkathIO.Rogue.IRequestHandler<TRequest>
    where TRequest : global::SkathIO.Rogue.IRequest<global::SkathIO.Rogue.Unit> { }
