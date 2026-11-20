using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue.Validation;

/// <summary>
/// Runs the <see cref="IAsyncRules{T}"/> resolved for a request. Called by generated dispatch code;
/// not meant to be called directly.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class AsyncRulesRunner
{
    /// <summary>
    /// Runs every rule in <paramref name="rules"/> against <paramref name="request"/> and throws
    /// <see cref="RogueValidationException"/> when any of them added an error. Returns a completed task,
    /// without allocating, when there is no rule.
    /// </summary>
    /// <typeparam name="T">The request type.</typeparam>
    /// <param name="rules">The rules resolved from DI, or null.</param>
    /// <param name="request">The request being validated.</param>
    /// <param name="cancellationToken">The dispatch's cancellation token.</param>
    public static ValueTask CheckAsync<T>(IEnumerable<IAsyncRules<T>>? rules, T request, CancellationToken cancellationToken)
    {
        // The interface checks below do not allocate, so a request with no async rules stays on the
        // allocation-free path.
        bool none = rules switch
        {
            null => true,
            IAsyncRules<T>[] array => array.Length == 0,
            ICollection<IAsyncRules<T>> collection => collection.Count == 0,
            _ => false,
        };
        return none ? default : RunAsync(rules!, request, cancellationToken);
    }

    private static async ValueTask RunAsync<T>(IEnumerable<IAsyncRules<T>> rules, T request, CancellationToken cancellationToken)
    {
        ValidationErrors errors = ValidationErrors.CreateShared();
        foreach (IAsyncRules<T> rule in rules)
        {
            ArgumentNullException.ThrowIfNull(rule);
            await rule.CheckAsync(request, errors, cancellationToken).ConfigureAwait(false);
        }
        errors.ThrowIfAny();
    }
}
