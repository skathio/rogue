namespace SkathIO.Rogue.Sample.WebApi;

/// <summary>
/// Result of the <c>/scope-probe</c> endpoint (FR-35). <see cref="CallCount"/> is the number of
/// records in the request-scoped <c>IHandlerCallTracker</c> after the endpoint records once — it is
/// always 1 because the scoped tracker is fresh per request scope.
/// </summary>
public sealed record ScopeProbeResult(int CallCount);
