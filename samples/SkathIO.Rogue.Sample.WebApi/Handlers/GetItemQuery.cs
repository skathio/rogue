using SkathIO.Rogue;

namespace SkathIO.Rogue.Sample.WebApi;

/// <summary>A query returning a result (FR-3).</summary>
public sealed record GetItemQuery(int Id) : IQuery<ItemResult>;

/// <summary>Result for <see cref="GetItemQuery"/>.</summary>
public sealed record ItemResult(int Id, string Name);
