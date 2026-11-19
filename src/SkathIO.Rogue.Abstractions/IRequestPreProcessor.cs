using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue;

/// <summary>Pre-processor that runs before the handler.</summary>
public interface IRequestPreProcessor<in TRequest>
    where TRequest : notnull
{
    /// <summary>Runs before the handler.</summary>
    ValueTask Process(TRequest request, CancellationToken cancellationToken);
}
