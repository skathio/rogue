using System;

namespace SkathIO.Rogue.Messaging;

/// <summary>The rules a message name follows, so every transport can carry it as a routing key.</summary>
public static class MessageLimits
{
    /// <summary>The longest message name, in characters.</summary>
    public const int MaxNameLength = 200;

    /// <summary>
    /// Whether <paramref name="name"/> is a valid message name: 1 to <see cref="MaxNameLength"/>
    /// characters, each an ASCII letter or digit, or one of <c>.</c>, <c>_</c>, <c>-</c> and <c>:</c>.
    /// </summary>
    public static bool IsValidName(string? name)
    {
        if (name is null || name.Length == 0 || name.Length > MaxNameLength)
        {
            return false;
        }
        foreach (char c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-' or ':'))
            {
                return false;
            }
        }
        return true;
    }

    internal static void CheckName(string? name, string parameter)
    {
        ArgumentNullException.ThrowIfNull(name, parameter);
        if (!IsValidName(name))
        {
            throw new ArgumentException("A message name must be 1 to " + MaxNameLength + " characters: ASCII letters, digits, '.', '_', '-' and ':'.", parameter);
        }
    }
}
