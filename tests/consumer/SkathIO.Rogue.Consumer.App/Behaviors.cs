using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue.Consumer.App;

/// <summary>Wraps every request; the generator finds it and weaves it in with no registration call.</summary>
public sealed class AuditBehavior<TRequest, TResponse>(ActivityLog log) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async ValueTask<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        string name = typeof(TRequest).Name;
        log.Record("before " + name);
        TResponse response = await next().ConfigureAwait(false);
        log.Record("after " + name);
        return response;
    }
}

/// <summary>Wraps the <see cref="CountTo"/> stream and multiplies each item by ten, so a test can tell it ran.</summary>
public sealed class TimesTenStreamBehavior(ActivityLog log) : IStreamPipelineBehavior<CountTo, int>
{
    public async IAsyncEnumerable<int> Handle(CountTo request, StreamHandlerDelegate<int> next, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        log.Record("stream " + nameof(CountTo));
        await foreach (int item in next().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return item * 10;
        }
    }
}
