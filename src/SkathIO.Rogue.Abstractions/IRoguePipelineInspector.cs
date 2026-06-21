using System;
using System.Collections.Generic;

namespace SkathIO.Rogue;

/// <summary>Provides runtime inspection of the resolved behavior pipeline for a request type.</summary>
public interface IRoguePipelineInspector
{
    /// <summary>Returns the ordered list of behaviors for <typeparamref name="TRequest"/>.</summary>
    /// <remarks>
    /// Constrained on <c>notnull</c> rather than a shared message marker: under the CQS clean break
    /// (PD-40) there is no common <c>IRequest</c>/<c>IBaseRequest</c> base shared by
    /// <see cref="ICommand{TResponse}"/> / <see cref="IQuery{TResponse}"/>, so the type parameter is
    /// the command/query type directly. Lookups for an unknown type return an empty list.
    /// </remarks>
    IReadOnlyList<BehaviorInfo> GetPipeline<TRequest>() where TRequest : notnull;

    /// <summary>Returns the ordered list of behaviors for the given request type.</summary>
    IReadOnlyList<BehaviorInfo> GetPipeline(Type requestType);
}

/// <summary>Describes a single behavior in a resolved pipeline.</summary>
/// <param name="BehaviorType">The concrete behavior type.</param>
/// <param name="Order">The resolved order (lower = outermost).</param>
/// <param name="Source">Registration source description (e.g. "open-generic" or "closed").</param>
public sealed record BehaviorInfo(Type BehaviorType, int Order, string Source);
