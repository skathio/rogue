using System;

namespace SkathIO.Rogue.Validation;

/// <summary>One failed check: where it failed, a stable code and a message for people.</summary>
public sealed class ValidationError
{
    /// <summary>Creates an error.</summary>
    /// <param name="path">The member path in ASP.NET form, such as <c>Customer.Name</c> or <c>Lines[2].Sku</c>.</param>
    /// <param name="code">A stable, machine-readable code, such as <c>not_empty</c>.</param>
    /// <param name="message">The message for people.</param>
    public ValidationError(string path, string code, string message)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(message);
        Path = path;
        Code = code;
        Message = message;
    }

    /// <summary>The member path in ASP.NET form, such as <c>Customer.Name</c> or <c>Lines[2].Sku</c>.</summary>
    public string Path { get; }

    /// <summary>A stable, machine-readable code, such as <c>not_empty</c>.</summary>
    public string Code { get; }

    /// <summary>The message for people.</summary>
    public string Message { get; }

    /// <inheritdoc />
    public override string ToString() => Path + ": " + Message;
}
