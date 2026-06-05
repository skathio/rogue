using System;
using System.Collections.Generic;

namespace SkathIO.Rogue;

/// <summary>Provides runtime inspection of the resolved behavior pipeline for a request type.</summary>
public interface IRoguePipelineInspector
{
    /// <summary>Returns the ordered list of behaviors for <typeparamref name="TRequest"/>.</summary>
    IReadOnlyList<BehaviorInfo> GetPipeline<TRequest>() where TRequest : IBaseRequest;

    /// <summary>Returns the ordered list of behaviors for the given request type.</summary>
    IReadOnlyList<BehaviorInfo> GetPipeline(Type requestType);
}

/// <summary>Describes a single behavior in a resolved pipeline.</summary>
/// <param name="BehaviorType">The concrete behavior type.</param>
/// <param name="Order">The resolved order (lower = outermost).</param>
/// <param name="Source">Registration source description (e.g. "open-generic" or "closed").</param>
public sealed record BehaviorInfo(Type BehaviorType, int Order, string Source);
