#if !NETSTANDARD2_0
namespace SkathIO.Rogue;

/// <summary>
/// Primary CQS marker for a streaming query that yields a sequence of <typeparamref name="TItem"/>
/// (returned as <see cref="System.Collections.Generic.IAsyncEnumerable{T}"/>). Independent contract —
/// derives from no shared stream-request marker (PD-40 streaming clean break). Streaming is always a
/// read-side operation, so there is no command analog.
/// </summary>
public interface IStreamQuery<out TItem> { }
#endif
