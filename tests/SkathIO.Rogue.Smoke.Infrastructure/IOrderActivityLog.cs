using System.Collections.Generic;

namespace SkathIO.Rogue.Smoke.Infrastructure;

/// <summary>
/// Records observable pipeline/handler activity so the smoke test can assert internal effects (event
/// fan-out, behavior wrapping) through an HTTP diagnostics endpoint rather than reaching into DI.
/// </summary>
public interface IOrderActivityLog
{
    void Record(string entry);

    IReadOnlyList<string> Entries { get; }

    void Clear();
}
