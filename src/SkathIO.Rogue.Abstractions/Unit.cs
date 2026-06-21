using System;
using System.Threading.Tasks;

namespace SkathIO.Rogue;

/// <summary>Represents a void response for requests that produce no value.</summary>
public readonly struct Unit : IEquatable<Unit>
{
    /// <summary>The singleton <see cref="Unit"/> value.</summary>
    public static readonly Unit Value = default;

    /// <summary>Returns a completed <see cref="System.Threading.Tasks.ValueTask{Unit}"/> with the <see cref="Unit"/> value.</summary>
    /// <remarks>Implemented as a property (not a field) to avoid a JIT circular-init issue with ValueTask&lt;Unit&gt;.</remarks>
#if NETSTANDARD2_0
    public static System.Threading.Tasks.ValueTask<Unit> Task => new System.Threading.Tasks.ValueTask<Unit>(default(Unit));
#else
    public static System.Threading.Tasks.ValueTask<Unit> Task => System.Threading.Tasks.ValueTask.FromResult(Value);
#endif

    /// <inheritdoc/>
    public bool Equals(Unit other) => true;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Unit;

    /// <inheritdoc/>
    public override int GetHashCode() => 0;

    /// <inheritdoc/>
    public override string ToString() => "()";

    /// <summary>Returns <see langword="true"/>; all <see cref="Unit"/> values are equal.</summary>
    public static bool operator ==(Unit left, Unit right) => true;

    /// <summary>Returns <see langword="false"/>; all <see cref="Unit"/> values are equal.</summary>
    public static bool operator !=(Unit left, Unit right) => false;
}
