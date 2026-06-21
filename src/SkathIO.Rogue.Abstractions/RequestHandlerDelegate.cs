using System.Threading.Tasks;

namespace SkathIO.Rogue;

/// <summary>
/// Delegate representing the next step in the pipeline. Call this to continue to the next behavior or handler.
/// </summary>
public delegate ValueTask<TResponse> RequestHandlerDelegate<TResponse>();
