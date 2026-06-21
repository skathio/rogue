using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using FluentValidation.Results;

namespace SkathIO.Rogue.Validation.FluentValidation;

/// <summary>
/// A pipeline behavior that runs every <see cref="IValidator{T}"/> registered for
/// <typeparamref name="TRequest"/> before the handler. All validators run; their failures are
/// aggregated; if any failure is found the behavior short-circuits — it throws
/// <see cref="ValidationException"/> <b>before</b> calling <c>next()</c>, so the handler never runs.
/// With zero validators registered the request passes straight through.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
public sealed class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly IValidator<TRequest>[] _validators;

    /// <summary>Initializes the behavior with the validators resolved from DI (zero is valid).</summary>
    /// <param name="validators">All <see cref="IValidator{T}"/> registered for <typeparamref name="TRequest"/>.</param>
    public ValidationBehavior(IEnumerable<IValidator<TRequest>> validators)
    {
        // Materialize once: IEnumerable from DI may be a lazily-resolved sequence.
        _validators = validators as IValidator<TRequest>[] ?? validators.ToArray();
    }

    /// <inheritdoc />
    public async ValueTask<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (_validators.Length == 0)
        {
            return await next().ConfigureAwait(false);
        }

        List<ValidationFailure>? failures = null;
        foreach (IValidator<TRequest> validator in _validators)
        {
            // Each validator gets a fresh context (via the T overload). A shared ValidationContext
            // accumulates failures across validators, so a later validator's result would report
            // earlier validators' failures too — double-counting on aggregation.
            ValidationResult result = await validator.ValidateAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.IsValid)
            {
                continue;
            }

            failures ??= new List<ValidationFailure>();
            foreach (ValidationFailure failure in result.Errors)
            {
                if (failure is not null)
                {
                    failures.Add(failure);
                }
            }
        }

        if (failures is { Count: > 0 })
        {
            // Short-circuit: throw before next() so the handler never runs. Default failure action
            // is throw; a Result<T> hook can replace this branch in a future iteration.
            throw new ValidationException(failures);
        }

        return await next().ConfigureAwait(false);
    }
}
