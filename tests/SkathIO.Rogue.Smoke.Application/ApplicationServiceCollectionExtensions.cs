using Microsoft.Extensions.DependencyInjection;

namespace SkathIO.Rogue.Smoke.Application;

public static class ApplicationServiceCollectionExtensions
{
    /// <summary>Registers this layer's services. Every <c>IValidator&lt;T&gt;</c> declared in this
    /// project's own source — <c>CreateOrderCommandValidator</c>, <c>MarkOrderShippedCommandValidator</c>
    /// — is discovered and registered automatically by
    /// <c>SkathIO.Rogue.Validation.FluentValidation.SourceGenerator</c>, the same compile-time,
    /// reflection-free mechanism that discovers commands, queries, and handlers: referencing the
    /// package and writing the validator is the entire contract, with no wiring call of any kind
    /// (no <c>AddValidatorsFromAssemblyContaining</c>, no <c>AddFluentValidation()</c> — see this
    /// work item's decisions.md D5). <c>ValidationBehavior&lt;,&gt;</c> itself and
    /// <see cref="Behaviors.OrderAuditBehavior{TRequest,TResponse}"/> need no explicit registration
    /// either — the generator auto-registers every behavior it discovers in this compilation
    /// (PD-17). There is nothing left for this method to do for validation; it exists for future
    /// layer-local services this project may add.</summary>
    public static IServiceCollection AddSmokeApplication(this IServiceCollection services)
    {
        return services;
    }
}
