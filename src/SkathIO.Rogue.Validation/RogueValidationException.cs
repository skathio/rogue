using System;
using System.Collections.Generic;
using System.Text;

namespace SkathIO.Rogue.Validation;

/// <summary>
/// Thrown by the generated dispatch when a <c>[Validate]</c> request fails validation. The handler has
/// not run. <see cref="ToProblemDictionary"/> gives the errors in the shape
/// <c>TypedResults.ValidationProblem</c> takes.
/// </summary>
public sealed class RogueValidationException : Exception
{
    /// <summary>Creates the exception for <paramref name="errors"/>.</summary>
    /// <param name="errors">The errors that failed the request; at least one.</param>
    public RogueValidationException(IReadOnlyList<ValidationError> errors)
        : base(BuildMessage(errors))
    {
        Errors = errors;
    }

    /// <summary>The errors that failed the request, in the order they were found.</summary>
    public IReadOnlyList<ValidationError> Errors { get; }

    /// <summary>
    /// Groups the error messages by path, for <c>TypedResults.ValidationProblem</c> or
    /// <c>ValidationProblemDetails</c>. Paths keep the order in which they first failed; messages keep
    /// the order in which they were added.
    /// </summary>
    public IDictionary<string, string[]> ToProblemDictionary()
    {
        var grouped = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (ValidationError error in Errors)
        {
            if (!grouped.TryGetValue(error.Path, out List<string>? messages))
            {
                messages = new List<string>();
                grouped.Add(error.Path, messages);
                order.Add(error.Path);
            }
            messages.Add(error.Message);
        }

        var problems = new Dictionary<string, string[]>(order.Count, StringComparer.Ordinal);
        foreach (string path in order)
        {
            problems.Add(path, grouped[path].ToArray());
        }
        return problems;
    }

    // Names the failing paths only: messages and values stay out of the exception message, so logging
    // it does not copy request data into the logs.
    private static string BuildMessage(IReadOnlyList<ValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var text = new StringBuilder("Validation failed with ");
        text.Append(errors.Count).Append(errors.Count == 1 ? " error" : " errors");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string separator = ": ";
        foreach (ValidationError error in errors)
        {
            if (seen.Add(error.Path))
            {
                text.Append(separator).Append(error.Path);
                separator = ", ";
            }
        }
        return text.Append('.').ToString();
    }
}
