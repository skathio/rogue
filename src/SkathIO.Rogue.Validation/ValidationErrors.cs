using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace SkathIO.Rogue.Validation;

/// <summary>
/// The errors collected while a request is validated. It allocates nothing until the first error is
/// added, which keeps the valid path free of allocations. The <c>Rules</c> hook receives it by
/// <c>ref</c>; an <see cref="IAsyncRules{T}"/> receives a copy that shares the same storage, so errors
/// added through either reach the same list.
/// </summary>
public struct ValidationErrors
{
    private List<ValidationError>? _items;

    // Storage allocated up front, so copies handed to async rules add to the same list.
    internal static ValidationErrors CreateShared() => new ValidationErrors { _items = new List<ValidationError>() };

    /// <summary>The number of errors added so far.</summary>
    public readonly int Count => _items?.Count ?? 0;

    /// <summary>True once any error has been added.</summary>
    public readonly bool HasErrors => Count != 0;

    /// <summary>The errors added so far, in the order they were added.</summary>
    public readonly IReadOnlyList<ValidationError> Items => (IReadOnlyList<ValidationError>?)_items ?? Array.Empty<ValidationError>();

    /// <summary>Adds an error.</summary>
    /// <param name="path">The member path, such as <c>nameof(Reference)</c>, <c>Customer.Name</c> or <c>Lines[2].Sku</c>.</param>
    /// <param name="code">A stable, machine-readable code.</param>
    /// <param name="message">The message for people.</param>
    public void Add(string path, string code, string message)
    {
        var error = new ValidationError(path, code, message);
        (_items ??= new List<ValidationError>()).Add(error);
    }

    /// <summary>
    /// Puts <paramref name="prefix"/> in front of the path of every error added from index
    /// <paramref name="start"/> on. Generated validators call it after checking a nested object or a
    /// collection item, so a path is built only when that part failed.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly void PrefixSince(int start, string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        if (_items is null)
        {
            return;
        }

        for (int i = Math.Max(start, 0); i < _items.Count; i++)
        {
            ValidationError error = _items[i];
            string path = error.Path.Length == 0 ? prefix
                : error.Path[0] == '[' ? prefix + error.Path
                : prefix + "." + error.Path;
            _items[i] = new ValidationError(path, error.Code, error.Message);
        }
    }

    /// <summary>Throws <see cref="RogueValidationException"/> when any error has been added.</summary>
    /// <exception cref="RogueValidationException">At least one error has been added.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly void ThrowIfAny()
    {
        if (_items is { Count: > 0 })
        {
            throw new RogueValidationException(_items.ToArray());
        }
    }
}
