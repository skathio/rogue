using System.Globalization;

namespace SkathIO.Rogue.Validation;

/// <summary>A number must be greater than the given limit. A null value passes.</summary>
public sealed class GreaterThanAttribute : RuleAttribute
{
    /// <summary>Requires a value greater than <paramref name="limit"/>.</summary>
    public GreaterThanAttribute(double limit)
    {
        Limit = limit;
    }

    /// <summary>The exclusive lower limit.</summary>
    public double Limit { get; }

    /// <summary>The error code this rule reports.</summary>
    public const string DefaultCode = "greater_than";

    /// <summary>The message this rule reports for <paramref name="propertyName"/>.</summary>
    public static string DefaultMessage(string propertyName, double limit)
        => "'" + propertyName + "' must be greater than " + limit.ToString(CultureInfo.InvariantCulture) + ".";

    /// <summary>True when <paramref name="value"/> is greater than <paramref name="limit"/>.</summary>
    public static bool IsValid(long value, double limit) => value > limit;

    /// <summary>True when <paramref name="value"/> is greater than <paramref name="limit"/>.</summary>
    public static bool IsValid(double value, double limit) => value > limit;

    /// <summary>True when <paramref name="value"/> is greater than <paramref name="limit"/>.</summary>
    public static bool IsValid(decimal value, double limit) => (double)value > limit;

    /// <summary>True when <paramref name="value"/> is null or greater than <paramref name="limit"/>.</summary>
    public static bool IsValid(long? value, double limit) => value is null || value.Value > limit;

    /// <summary>True when <paramref name="value"/> is null or greater than <paramref name="limit"/>.</summary>
    public static bool IsValid(double? value, double limit) => value is null || value.Value > limit;

    /// <summary>True when <paramref name="value"/> is null or greater than <paramref name="limit"/>.</summary>
    public static bool IsValid(decimal? value, double limit) => value is null || (double)value.Value > limit;
}

/// <summary>A number must be less than the given limit. A null value passes.</summary>
public sealed class LessThanAttribute : RuleAttribute
{
    /// <summary>Requires a value less than <paramref name="limit"/>.</summary>
    public LessThanAttribute(double limit)
    {
        Limit = limit;
    }

    /// <summary>The exclusive upper limit.</summary>
    public double Limit { get; }

    /// <summary>The error code this rule reports.</summary>
    public const string DefaultCode = "less_than";

    /// <summary>The message this rule reports for <paramref name="propertyName"/>.</summary>
    public static string DefaultMessage(string propertyName, double limit)
        => "'" + propertyName + "' must be less than " + limit.ToString(CultureInfo.InvariantCulture) + ".";

    /// <summary>True when <paramref name="value"/> is less than <paramref name="limit"/>.</summary>
    public static bool IsValid(long value, double limit) => value < limit;

    /// <summary>True when <paramref name="value"/> is less than <paramref name="limit"/>.</summary>
    public static bool IsValid(double value, double limit) => value < limit;

    /// <summary>True when <paramref name="value"/> is less than <paramref name="limit"/>.</summary>
    public static bool IsValid(decimal value, double limit) => (double)value < limit;

    /// <summary>True when <paramref name="value"/> is null or less than <paramref name="limit"/>.</summary>
    public static bool IsValid(long? value, double limit) => value is null || value.Value < limit;

    /// <summary>True when <paramref name="value"/> is null or less than <paramref name="limit"/>.</summary>
    public static bool IsValid(double? value, double limit) => value is null || value.Value < limit;

    /// <summary>True when <paramref name="value"/> is null or less than <paramref name="limit"/>.</summary>
    public static bool IsValid(decimal? value, double limit) => value is null || (double)value.Value < limit;
}
