using SkathIO.Rogue;

namespace SkathIO.Rogue.Sample.WebApi;

/// <summary>A no-response command (FR-2). Dispatches via the <c>ICommand</c> → <c>ValueTask</c> path.</summary>
public sealed record SilentCommand(string Payload) : ICommand;
