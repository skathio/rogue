#if !NETSTANDARD2_0
namespace SkathIO.Rogue.Compatibility;

// MediatR-shaped streaming request marker (the adapter is the sole home of this surface — the core
// deleted IStreamRequest<T>/IBaseStreamRequest under the D5 clean break, PD-40 amendment).
//
// PD-48: streaming carries no command-vs-query ambiguity (a stream is always read-side; [MapAsQuery]
// does not apply, ROGUE012 never fires for streams — PD-43 amendment). The F8 mapping
// IStreamRequest<T> -> IStreamQuery<T> is therefore unconditional, so the marker is declared as a thin
// "is-a" sub-interface of the core IStreamQuery<T>. A type implementing only this adapter marker IS a
// core IStreamQuery<T>: the generator's existing IStreamQuery-keyed discovery records it as a stream
// message and the dispatcher's CreateStream<TItem> switch matches it with no adapter-specific emission.
public interface IStreamRequest<out TResponse> : global::SkathIO.Rogue.IStreamQuery<TResponse> { }
#endif
