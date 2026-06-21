using System;

namespace SkathIO.Rogue;

/// <summary>
/// Declares the pipeline order of an <see cref="IPipelineBehavior{TRequest,TResponse}"/> or
/// <see cref="IStreamPipelineBehavior{TRequest,TResponse}"/> implementation. Lower values are
/// woven further out (executed first); behaviors without this attribute default to
/// <see cref="Order"/> = 0 (PD-4 / PD-13a). Lives in <c>SkathIO.Rogue.Abstractions</c> so behavior
/// authors can annotate their types without depending on the full <c>SkathIO.Rogue</c> package.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class BehaviorOrderAttribute : Attribute
{
    /// <summary>Initializes a new instance with the given pipeline order.</summary>
    /// <param name="order">The order; lower = outermost (executed first).</param>
    public BehaviorOrderAttribute(int order) => Order = order;

    /// <summary>The pipeline order. Lower values are woven further out (executed first).</summary>
    public int Order { get; }
}
