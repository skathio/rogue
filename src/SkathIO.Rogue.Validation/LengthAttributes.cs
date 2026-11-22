using System.Collections.Generic;

namespace SkathIO.Rogue.Validation;

/// <summary>
/// A string must have at least the given number of characters, or a collection at least the given
/// number of items. A null value passes: combine with <see cref="NotEmptyAttribute"/> to require one.
/// </summary>
public sealed class MinimumLengthAttribute : RuleAttribute
{
    /// <summary>Requires at least <paramref name="length"/> characters or items.</summary>
    public MinimumLengthAttribute(int length)
    {
        Length = length;
    }

    /// <summary>The minimum number of characters or items.</summary>
    public int Length { get; }

    /// <summary>The error code this rule reports.</summary>
    public const string DefaultCode = "minimum_length";

    /// <summary>The message this rule reports for <paramref name="propertyName"/>.</summary>
    public static string DefaultMessage(string propertyName, int length)
        => "'" + propertyName + "' must have at least " + length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " characters or items.";

    /// <summary>True when <paramref name="value"/> is null or has at least <paramref name="length"/> characters.</summary>
    public static bool IsValid(string? value, int length) => value is null || value.Length >= length;

    /// <summary>True when <paramref name="value"/> is null or has at least <paramref name="length"/> items.</summary>
    public static bool IsValid<T>(IReadOnlyCollection<T>? value, int length) => value is null || value.Count >= length;
}

/// <summary>
/// A string must have at most the given number of characters, or a collection at most the given
/// number of items. A null value passes.
/// </summary>
public sealed class MaximumLengthAttribute : RuleAttribute
{
    /// <summary>Allows at most <paramref name="length"/> characters or items.</summary>
    public MaximumLengthAttribute(int length)
    {
        Length = length;
    }

    /// <summary>The maximum number of characters or items.</summary>
    public int Length { get; }

    /// <summary>The error code this rule reports.</summary>
    public const string DefaultCode = "maximum_length";

    /// <summary>The message this rule reports for <paramref name="propertyName"/>.</summary>
    public static string DefaultMessage(string propertyName, int length)
        => "'" + propertyName + "' must have at most " + length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " characters or items.";

    /// <summary>True when <paramref name="value"/> is null or has at most <paramref name="length"/> characters.</summary>
    public static bool IsValid(string? value, int length) => value is null || value.Length <= length;

    /// <summary>True when <paramref name="value"/> is null or has at most <paramref name="length"/> items.</summary>
    public static bool IsValid<T>(IReadOnlyCollection<T>? value, int length) => value is null || value.Count <= length;
}
