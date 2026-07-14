using System.Threading;
using System.Threading.Tasks;
using SkathIO.Rogue.Smoke.Infrastructure;

namespace SkathIO.Rogue.Smoke.Application.Behaviors;

/// <summary>
/// A custom open-generic pipeline behavior wrapping every request in this compilation, ordered
/// OUTSIDE the auto-woven FluentValidation <c>ValidationBehavior&lt;,&gt;</c> (which defaults to
/// order 0) so a request that fails validation still records an "entering"/"failed" pair — proving
/// the behavior chain and <c>[BehaviorOrder]</c> both work in a realistic multi-behavior pipeline.
/// </summary>
[BehaviorOrder(-10)]
public sealed class OrderAuditBehavior<TRequest, TResponse>(IOrderActivityLog log)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async ValueTask<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        string name = typeof(TRequest).Name;
        log.Record($"OrderAuditBehavior: entering {name}");
        try
        {
            TResponse response = await next().ConfigureAwait(false);
            log.Record($"OrderAuditBehavior: completed {name}");
            return response;
        }
        catch
        {
            log.Record($"OrderAuditBehavior: failed {name}");
            throw;
        }
    }
}
