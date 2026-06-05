using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue;

/// <summary>Pre-processor that runs before the handler.</summary>
public interface IRequestPreProcessor<in TRequest>
    where TRequest : notnull
{
    /// <summary>Runs before the handler.</summary>
#if NETSTANDARD2_0
    ValueTask<Unit> Process(TRequest request, CancellationToken cancellationToken);
#else
    ValueTask Process(TRequest request, CancellationToken cancellationToken);
#endif
}
