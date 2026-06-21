#if !NETSTANDARD2_0
namespace SkathIO.Rogue.Compatibility;

// MediatR-shaped streaming request handler. PD-48: declared as a thin "is-a" sub-interface of the core
// IStreamQueryHandler<TRequest, TItem> (the F8 IStreamRequest<T> -> IStreamQuery<T> mapping is
// unconditional — streams are always read-side). The core Handle signature
// (IAsyncEnumerable<TItem> Handle(TRequest, CancellationToken)) already matches MediatR's exactly, so
// no Handle is redeclared here: an implementer satisfies the inherited IStreamQueryHandler<,>.Handle.
// Because the type IS-A core stream handler, the generator's existing IStreamQueryHandler-keyed
// discovery loop finds it and the dispatcher's CreateStream switch dispatches it with no
// adapter-specific code.
//
// net8+ only: the core IStreamQuery<T>/IStreamQueryHandler<,> it extends do not exist on
// netstandard2.0 (mirrors the core's own #if guard).
public interface IStreamRequestHandler<in TRequest, TItem>
    : global::SkathIO.Rogue.IStreamQueryHandler<TRequest, TItem>
    where TRequest : IStreamRequest<TItem>
{
}
#endif
