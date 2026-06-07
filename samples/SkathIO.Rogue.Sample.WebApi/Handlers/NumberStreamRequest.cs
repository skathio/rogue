using SkathIO.Rogue;

namespace SkathIO.Rogue.Sample.WebApi;

/// <summary>A streaming request yielding <see cref="Count"/> integers (FR-5).</summary>
public sealed record NumberStreamRequest(int Count) : IStreamRequest<int>;
