using SkathIO.Rogue;

namespace SkathIO.Rogue.Sample.WebApi;

/// <summary>Strongly-typed request returning a response (FR-1).</summary>
public sealed record PingRequest(string Message) : ICommand<PingResponse>;

/// <summary>Response for <see cref="PingRequest"/>.</summary>
public sealed record PingResponse(string Echo);
