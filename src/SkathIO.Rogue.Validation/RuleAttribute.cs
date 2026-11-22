using System;

namespace SkathIO.Rogue.Validation;

/// <summary>
/// Base class of every validation rule attribute. A rule is a marker: its logic lives in static
/// members that the generated validator calls directly, so no attribute instance is created at run time.
/// A rule type provides
/// <list type="bullet">
/// <item><description><c>public static bool IsValid(TValue value, ...)</c>, one overload per property type it
/// supports, whose extra parameters take the attribute's constructor arguments in order;</description></item>
/// <item><description><c>public const string DefaultCode</c>, the error code;</description></item>
/// <item><description><c>public static string DefaultMessage(string propertyName, ...)</c>, the message, built only
/// when the check fails.</description></item>
/// </list>
/// A rule that does not fit the property it is placed on is a build error.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public abstract class RuleAttribute : Attribute
{
    /// <summary>Initializes the rule.</summary>
    protected RuleAttribute()
    {
    }

    /// <summary>The error code to report instead of the rule's <c>DefaultCode</c>.</summary>
    public string? Code { get; set; }

    /// <summary>The message to report instead of the rule's <c>DefaultMessage</c>.</summary>
    public string? Message { get; set; }
}
