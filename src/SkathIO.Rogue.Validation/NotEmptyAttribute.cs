using System;
using System.Collections;
using System.Collections.Generic;

namespace SkathIO.Rogue.Validation;

/// <summary>
/// The value must not be null, an empty or whitespace-only string, an empty collection or
/// <see cref="Guid.Empty"/>.
/// </summary>
public sealed class NotEmptyAttribute : RuleAttribute
{
    /// <summary>The error code this rule reports.</summary>
    public const string DefaultCode = "not_empty";

    /// <summary>The message this rule reports for <paramref name="propertyName"/>.</summary>
    public static string DefaultMessage(string propertyName) => "'" + propertyName + "' must not be empty.";

    /// <summary>True when <paramref name="value"/> holds a non-whitespace character.</summary>
    public static bool IsValid(string? value) => !string.IsNullOrWhiteSpace(value);

    /// <summary>True when <paramref name="value"/> is not <see cref="Guid.Empty"/>.</summary>
    public static bool IsValid(Guid value) => value != Guid.Empty;

    /// <summary>True when <paramref name="value"/> is a collection with at least one item.</summary>
    public static bool IsValid<T>(IEnumerable<T>? value)
    {
        // Count through the collection interfaces first so the common collection types are checked
        // without allocating an enumerator.
        return value switch
        {
            null => false,
            ICollection<T> collection => collection.Count != 0,
            IReadOnlyCollection<T> readOnly => readOnly.Count != 0,
            ICollection untyped => untyped.Count != 0,
            _ => HasAny(value),
        };
    }

    private static bool HasAny<T>(IEnumerable<T> value)
    {
        using IEnumerator<T> enumerator = value.GetEnumerator();
        return enumerator.MoveNext();
    }
}
