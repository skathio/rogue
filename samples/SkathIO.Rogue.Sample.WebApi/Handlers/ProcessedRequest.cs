using System.Threading;
using System.Threading.Tasks;
using SkathIO.Rogue;

namespace SkathIO.Rogue.Sample.WebApi;

/// <summary>
/// Request used to exercise the FR-25 pre/post-processor bridge. Its handler and the surrounding
/// pre/post processors each record into the scoped <see cref="IHandlerCallTracker"/> so a test can
/// assert that all three ran, in deterministic order, through the generated dispatch loop.
/// </summary>
public sealed record ProcessedRequest(string Value) : ICommand<ProcessedResponse>;

/// <summary>Response for <see cref="ProcessedRequest"/>.</summary>
public sealed record ProcessedResponse(string Value);

/// <summary>Handler for <see cref="ProcessedRequest"/>; records its own invocation (FR-25).</summary>
public sealed class ProcessedRequestHandler : ICommandHandler<ProcessedRequest, ProcessedResponse>
{
    /// <summary>Marker recorded when the handler runs.</summary>
    public const string Marker = "processed:handler";

    private readonly IHandlerCallTracker _tracker;

    /// <summary>Initializes the handler with the scoped tracker.</summary>
    public ProcessedRequestHandler(IHandlerCallTracker tracker) => _tracker = tracker;

    /// <inheritdoc />
    public ValueTask<ProcessedResponse> Handle(ProcessedRequest request, CancellationToken cancellationToken)
    {
        _tracker.Record(Marker);
        return new ValueTask<ProcessedResponse>(new ProcessedResponse(request.Value));
    }
}

/// <summary>
/// FR-25 pre-processor for <see cref="ProcessedRequest"/>. Records before the behavior pipeline runs.
/// </summary>
public sealed class RecordingPreProcessor : IRequestPreProcessor<ProcessedRequest>
{
    /// <summary>Marker recorded when the pre-processor runs.</summary>
    public const string Marker = "processed:pre";

    private readonly IHandlerCallTracker _tracker;

    /// <summary>Initializes the pre-processor with the scoped tracker.</summary>
    public RecordingPreProcessor(IHandlerCallTracker tracker) => _tracker = tracker;

    /// <inheritdoc />
#if NETSTANDARD2_0
    public ValueTask<Unit> Process(ProcessedRequest request, CancellationToken cancellationToken)
    {
        _tracker.Record(Marker);
        return Unit.Task;
    }
#else
    public ValueTask Process(ProcessedRequest request, CancellationToken cancellationToken)
    {
        _tracker.Record(Marker);
        return default;
    }
#endif
}

/// <summary>
/// FR-25 post-processor for <see cref="ProcessedRequest"/>. Records after the handler completes.
/// </summary>
public sealed class RecordingPostProcessor : IRequestPostProcessor<ProcessedRequest, ProcessedResponse>
{
    /// <summary>Marker recorded when the post-processor runs.</summary>
    public const string Marker = "processed:post";

    private readonly IHandlerCallTracker _tracker;

    /// <summary>Initializes the post-processor with the scoped tracker.</summary>
    public RecordingPostProcessor(IHandlerCallTracker tracker) => _tracker = tracker;

    /// <inheritdoc />
#if NETSTANDARD2_0
    public ValueTask<Unit> Process(ProcessedRequest request, ProcessedResponse response, CancellationToken cancellationToken)
    {
        _tracker.Record(Marker);
        return Unit.Task;
    }
#else
    public ValueTask Process(ProcessedRequest request, ProcessedResponse response, CancellationToken cancellationToken)
    {
        _tracker.Record(Marker);
        return default;
    }
#endif
}
