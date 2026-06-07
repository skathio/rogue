namespace SkathIO.Rogue.Compatibility;

// Extends the Rogue marker so the generator discovers implementing types automatically.
public interface IRequest<TResponse> : global::SkathIO.Rogue.IRequest<TResponse> { }
public interface IRequest : global::SkathIO.Rogue.IRequest { }
