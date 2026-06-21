using System.Collections.Generic;

namespace SkathIO.Rogue.Sample.WebApi;

/// <summary>
/// DI-scoped observation seam for handler invocations (spec §6.4). Declared in the host so both the
/// host's handlers (which inject it) and the WAF test project (which reads it back from a resolved
/// scope) can see it. Replaces static mutable handler state — a shared <c>IClassFixture</c> would
/// otherwise leak static counters across tests (Major #7).
/// </summary>
public interface IHandlerCallTracker
{
    /// <summary>Records that a handler ran.</summary>
    void Record(string handlerName);

    /// <summary>The handler names recorded in this scope, in invocation order.</summary>
    IReadOnlyList<string> Calls { get; }
}

/// <summary>Scoped, per-request implementation of <see cref="IHandlerCallTracker"/>.</summary>
public sealed class HandlerCallTracker : IHandlerCallTracker
{
    private readonly List<string> _calls = new();

    /// <inheritdoc />
    public void Record(string handlerName) => _calls.Add(handlerName);

    /// <inheritdoc />
    public IReadOnlyList<string> Calls => _calls;
}
