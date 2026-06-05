#if !NETSTANDARD2_0
namespace SkathIO.Rogue;

/// <summary>Marker for a streaming request that returns <see cref="System.Collections.Generic.IAsyncEnumerable{T}"/>.</summary>
public interface IStreamRequest<out TResponse> : IBaseStreamRequest { }

/// <summary>Semantic marker for a streaming query.</summary>
public interface IStreamQuery<out TResponse> : IStreamRequest<TResponse> { }

/// <summary>Base marker for all streaming request types.</summary>
public interface IBaseStreamRequest { }
#endif
