using System.Collections.Generic;
using System.Threading;
using SkathIO.Rogue;

namespace SkathIO.Rogue.Sample.WebApi;

/// <summary>Streams <c>Count</c> integers for <see cref="NumberStreamRequest"/> (FR-5, FR-11).</summary>
public sealed class NumberStreamHandler : IStreamQueryHandler<NumberStreamRequest, int>
{
    /// <inheritdoc />
#pragma warning disable CS1998 // async iterator has no awaits — the stream is synchronous by design.
    public async IAsyncEnumerable<int> Handle(
        NumberStreamRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (int i = 0; i < request.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return i;
        }
    }
#pragma warning restore CS1998
}
