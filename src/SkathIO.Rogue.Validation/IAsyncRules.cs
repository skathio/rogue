using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue.Validation;

/// <summary>
/// Rules for <typeparamref name="T"/> that need services or I/O, such as a database lookup. Implementations
/// are resolved from DI in the request's scope; the generator registers the ones it finds in the
/// project as Scoped. They run after the attribute rules and the <c>Rules</c> hook have passed, so they
/// can assume the request is well-formed.
/// </summary>
/// <typeparam name="T">The <c>[Validate]</c> request type.</typeparam>
public interface IAsyncRules<in T>
{
    /// <summary>Checks <paramref name="request"/> and adds any error to <paramref name="errors"/>.</summary>
    /// <param name="request">The request being validated.</param>
    /// <param name="errors">Where to add errors; it shares storage with the caller's copy.</param>
    /// <param name="cancellationToken">The dispatch's cancellation token.</param>
    ValueTask CheckAsync(T request, ValidationErrors errors, CancellationToken cancellationToken);
}
