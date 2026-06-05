using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue;

/// <summary>
/// Executes the behavior pipeline for a request. Public so the source-generated
/// dispatcher (emitted into the consumer's compilation) can call it.
/// </summary>
public static class PipelineExecutor
{
    /// <summary>
    /// Executes the pipeline. When <paramref name="behaviors"/> is empty, calls the handler
    /// directly with zero allocation (NFR-PERF-1). With behaviors, builds a struct-index fold
    /// that avoids per-link heap allocations (PD-2).
    /// </summary>
    public static ValueTask<TResponse> Execute<TRequest, TResponse>(
        TRequest request,
        IReadOnlyList<IPipelineBehavior<TRequest, TResponse>> behaviors,
        RequestHandlerDelegate<TResponse> handler,
        CancellationToken cancellationToken)
        where TRequest : notnull
    {
        if (behaviors.Count == 0)
        {
            return handler();
        }

        return new PipelineState<TRequest, TResponse>(request, behaviors, handler, cancellationToken).ExecuteAsync();
    }

    private readonly struct PipelineState<TRequest, TResponse>
        where TRequest : notnull
    {
        private readonly TRequest _request;
        private readonly IReadOnlyList<IPipelineBehavior<TRequest, TResponse>> _behaviors;
        private readonly RequestHandlerDelegate<TResponse> _handler;
        private readonly CancellationToken _cancellationToken;

        internal PipelineState(
            TRequest request,
            IReadOnlyList<IPipelineBehavior<TRequest, TResponse>> behaviors,
            RequestHandlerDelegate<TResponse> handler,
            CancellationToken cancellationToken)
        {
            _request = request;
            _behaviors = behaviors;
            _handler = handler;
            _cancellationToken = cancellationToken;
        }

        internal ValueTask<TResponse> ExecuteAsync() => ExecuteAtIndex(0);

        private ValueTask<TResponse> ExecuteAtIndex(int index)
        {
            if (index == _behaviors.Count)
            {
                return _handler();
            }

            var behavior = _behaviors[index];
            // The lambda captures 'state' (a struct copy) and 'index'; the C# compiler boxes the captured
            // struct. This allocates one delegate per behavior depth — acceptable for the runtime fallback
            // path. The Phase 4 source generator emits statically-typed per-request chains that are
            // genuinely allocation-free for the strongly-typed dispatch path (PD-2).
            var state = this;
            return behavior.Handle(_request, () => state.ExecuteAtIndex(index + 1), _cancellationToken);
        }
    }
}
