using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace SkathIO.Rogue.Validation.FluentValidation.SourceGenerator;

/// <summary>
/// An immutable array wrapper with element-wise value equality, safe for use in incremental
/// generator model records. Plain <see cref="ImmutableArray{T}"/> uses reference equality in record
/// <c>==</c> comparisons, which defeats the incremental generator cache — this project cannot
/// reference the core generator's own copy of this type (<c>SkathIO.Rogue.SourceGenerator</c> is only
/// ever consumed as <c>OutputItemType="Analyzer" ReferenceOutputAssembly="false"</c>, which excludes it
/// from ordinary compile-time references), so it is duplicated here deliberately.
/// </summary>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    private readonly ImmutableArray<T> _array;

    public EquatableArray(ImmutableArray<T> array) => _array = array;

    public static readonly EquatableArray<T> Empty = new(ImmutableArray<T>.Empty);

    public int Count => _array.IsDefaultOrEmpty ? 0 : _array.Length;

    public T this[int index] => _array[index];

    public bool Equals(EquatableArray<T> other)
    {
        if (_array.IsDefaultOrEmpty && other._array.IsDefaultOrEmpty) return true;
        if (_array.IsDefaultOrEmpty != other._array.IsDefaultOrEmpty) return false;
        if (_array.Length != other._array.Length) return false;
        for (int i = 0; i < _array.Length; i++)
            if (!EqualityComparer<T>.Default.Equals(_array[i], other._array[i])) return false;
        return true;
    }

    public override bool Equals(object? obj)
        => obj is EquatableArray<T> other && Equals(other);

    // System.HashCode is netstandard2.1+ only; the generator targets netstandard2.0,
    // so use a manual polynomial hash.
    public override int GetHashCode()
    {
        if (_array.IsDefaultOrEmpty) return 0;
        unchecked
        {
            int hash = 17;
            foreach (var item in _array)
                hash = hash * 31 + (item?.GetHashCode() ?? 0);
            return hash;
        }
    }

    public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right)
        => left.Equals(right);

    public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right)
        => !left.Equals(right);

    public ImmutableArray<T> ToImmutableArray() => _array;

    public IEnumerator<T> GetEnumerator()
        => ((IEnumerable<T>)(_array.IsDefaultOrEmpty ? ImmutableArray<T>.Empty : _array)).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public static EquatableArray<T> From(ImmutableArray<T> array) => new(array);
}
