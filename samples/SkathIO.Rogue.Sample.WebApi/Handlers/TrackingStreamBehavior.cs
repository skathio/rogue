using System.Collections.Generic;
using System.Threading;
using SkathIO.Rogue;

namespace SkathIO.Rogue.Sample.WebApi;

/// <summary>
/// A pass-through <see cref="IStreamPipelineBehavior{TRequest,TResponse}"/> for
/// <see cref="NumberStreamRequest"/> that records into the scoped <see cref="IHandlerCallTracker"/>
/// once before the inner stream starts and once per yielded element. Declared in the host source so
/// the generator (running in the host compilation) discovers and weaves it onto the stream path
/// (FR-23, Phase 4.2.1 stream weaving). It is a closed behavior (not open-generic), so it applies
/// only to the single stream request type and leaves every other dispatch untouched.
/// </summary>
public sealed class TrackingStreamBehavior : IStreamPipelineBehavior<NumberStreamRequest, int>
{
    /// <summary>Recorded before the inner stream is enumerated.</summary>
    public const string Wrapped = "stream-behavior:wrapped";

    /// <summary>Recorded once per element yielded by the inner stream.</summary>
    public const string PerItem = "stream-behavior:item";

    private readonly IHandlerCallTracker _tracker;

    /// <summary>Initializes the behavior with the scoped tracker (FR-24 constructor DI).</summary>
    public TrackingStreamBehavior(IHandlerCallTracker tracker) => _tracker = tracker;

    /// <inheritdoc />
    public async IAsyncEnumerable<int> Handle(
        NumberStreamRequest request,
        StreamHandlerDelegate<int> next,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _tracker.Record(Wrapped);
        await foreach (int item in next())
        {
            _tracker.Record(PerItem);
            yield return item;
        }
    }
}
