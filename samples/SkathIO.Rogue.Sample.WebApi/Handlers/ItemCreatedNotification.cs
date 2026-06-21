using SkathIO.Rogue;

namespace SkathIO.Rogue.Sample.WebApi;

/// <summary>A notification fanned out to two-or-more handlers (FR-4).</summary>
public sealed record ItemCreatedNotification(int Id) : IEvent;
