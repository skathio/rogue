using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue;
using SkathIO.Rogue.Validation.FluentValidation;

namespace SkathIO.Rogue.Benchmarks.Validation;

// ── Rogue side: command/handler/validator triple, mirroring
// tests/SkathIO.Rogue.DiResolution.Tests/Fixtures.cs's CreateUserCommand shape (real
// AbstractValidator rule, not a hand-rolled stand-in). ─────────────────────────────────

/// <summary>Command exercised by <see cref="ValidationBenchmarks.Rogue_ValidatedNoOp"/>.</summary>
public sealed record CreateUserCommand(string Name) : ICommand<string>;

/// <summary>No-op handler — deterministic response, no allocation beyond the string concat.</summary>
public sealed class CreateUserHandler : ICommandHandler<CreateUserCommand, string>
{
    public ValueTask<string> Handle(CreateUserCommand request, CancellationToken ct)
        => new("created:" + request.Name);
}

/// <summary>A genuine FluentValidation rule (not an always-pass stand-in) so the steady-state
/// valid-payload benchmark exercises the real validator invocation, not a no-op.</summary>
public sealed class CreateUserValidator : AbstractValidator<CreateUserCommand>
{
    public CreateUserValidator()
        => RuleFor(x => x.Name).NotEmpty().WithMessage("Name is required");
}

// ── MediatR side: parallel command/handler/validator, byte-identical bodies. ───────────

/// <summary>MediatR request mirroring <see cref="CreateUserCommand"/>.</summary>
public sealed record MediatRCreateUserCommand(string Name) : global::MediatR.IRequest<string>;

/// <summary>MediatR handler — byte-identical no-op body to <see cref="CreateUserHandler"/>.</summary>
public sealed class MediatRCreateUserHandler : global::MediatR.IRequestHandler<MediatRCreateUserCommand, string>
{
    public Task<string> Handle(MediatRCreateUserCommand request, CancellationToken ct)
        => Task.FromResult("created:" + request.Name);
}

/// <summary>Same rule as <see cref="CreateUserValidator"/>, for the MediatR-side command type.</summary>
public sealed class MediatRCreateUserValidator : AbstractValidator<MediatRCreateUserCommand>
{
    public MediatRCreateUserValidator()
        => RuleFor(x => x.Name).NotEmpty().WithMessage("Name is required");
}

/// <summary>
/// MediatR has no built-in validation pipeline. This hand-rolls the well-known community pattern —
/// resolve every <see cref="IValidator{T}"/> registered for <typeparamref name="TRequest"/>, run
/// them all, aggregate failures, throw <see cref="ValidationException"/> before <c>next()</c> if any
/// fail — matching <see cref="ValidationBehavior{TRequest,TResponse}"/>'s semantics as closely as
/// possible so the two benchmarks measure the same amount of validation work.
/// </summary>
public sealed class MediatRValidationBehavior<TRequest, TResponse> : global::MediatR.IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly IValidator<TRequest>[] _validators;

    public MediatRValidationBehavior(IEnumerable<IValidator<TRequest>> validators)
        => _validators = validators as IValidator<TRequest>[] ?? validators.ToArray();

    public async Task<TResponse> Handle(
        TRequest request,
        global::MediatR.RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (_validators.Length == 0)
        {
            return await next().ConfigureAwait(false);
        }

        List<ValidationFailure>? failures = null;
        foreach (IValidator<TRequest> validator in _validators)
        {
            ValidationResult result = await validator.ValidateAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.IsValid)
            {
                continue;
            }

            failures ??= new List<ValidationFailure>();
            failures.AddRange(result.Errors);
        }

        if (failures is { Count: > 0 })
        {
            throw new ValidationException(failures);
        }

        return await next().ConfigureAwait(false);
    }
}

/// <summary>
/// Scenario — the actual scenario this work item is about: a pipeline behavior with a Scoped
/// dependency (a FluentValidation <c>IValidator&lt;T&gt;</c>), steady-state <b>valid</b> payload (no
/// throw — exception-path benchmarking is not representative under BenchmarkDotNet). Rogue vs.
/// MediatR, both wired with equivalent validation behaviors.
/// </summary>
/// <remarks>
/// Deliberately isolated into its own project (D10) rather than added to
/// <c>SkathIO.Rogue.Benchmarks</c> — see this project's <c>.csproj</c> comment for why referencing
/// <c>SkathIO.Rogue.Validation.FluentValidation</c> in the shared bench project would have
/// contaminated the zero-behavior headline benchmarks there.
/// </remarks>
[MemoryDiagnoser]
public class ValidationBenchmarks
{
    private ISender _rogue = null!;
    private global::MediatR.ISender _mediatR = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rogueServices = new ServiceCollection();
        rogueServices.AddRogue(o => o.AddOpenBehavior(typeof(ValidationBehavior<,>)));
        rogueServices.AddTransient<ICommandHandler<CreateUserCommand, string>, CreateUserHandler>();
        rogueServices.AddScoped<IValidator<CreateUserCommand>, CreateUserValidator>();
        _rogue = rogueServices.BuildServiceProvider().GetRequiredService<ISender>();

        var mediatRServices = new ServiceCollection();
        mediatRServices.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<MediatRCreateUserHandler>();
            cfg.AddOpenBehavior(typeof(MediatRValidationBehavior<,>));
        });
        mediatRServices.AddScoped<IValidator<MediatRCreateUserCommand>, MediatRCreateUserValidator>();
        _mediatR = mediatRServices.BuildServiceProvider().GetRequiredService<global::MediatR.ISender>();
    }

    [Benchmark(Baseline = true)]
    public ValueTask<string> Rogue_ValidatedNoOp()
        => _rogue.Send(new CreateUserCommand("valid-name"));

    [Benchmark]
    public Task<string> MediatR_ValidatedNoOp()
        => _mediatR.Send(new MediatRCreateUserCommand("valid-name"));
}
