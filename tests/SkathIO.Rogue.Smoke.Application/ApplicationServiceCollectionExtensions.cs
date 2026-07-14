using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue.Smoke.Application.Orders;

namespace SkathIO.Rogue.Smoke.Application;

public static class ApplicationServiceCollectionExtensions
{
    /// <summary>Registers this layer's services. <see cref="AddValidatorsFromAssemblyContaining{T}"/>
    /// (from the separate <c>FluentValidation.DependencyInjectionExtensions</c> package) scans this
    /// assembly once at startup and registers every <see cref="IValidator{T}"/> implementation it
    /// finds — no per-validator registration line, and no per-validator change when a new command
    /// gains one. <c>ValidationBehavior&lt;,&gt;</c> itself and
    /// <see cref="Behaviors.OrderAuditBehavior{TRequest,TResponse}"/> need no explicit registration
    /// either — the generator auto-registers every behavior it discovers in this compilation
    /// (PD-17). This is a one-time startup scan, not per-dispatch reflection, so it doesn't touch
    /// the reflection-free dispatch path (docs/governance.md "Quality bar").</summary>
    public static IServiceCollection AddSmokeApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssemblyContaining<CreateOrderCommandValidator>();
        return services;
    }
}
