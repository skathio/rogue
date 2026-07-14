using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue.Smoke.Application.Orders;

namespace SkathIO.Rogue.Smoke.Application;

public static class ApplicationServiceCollectionExtensions
{
    /// <summary>Registers this layer's services. The validator is registered by hand, Scoped — this
    /// repo does not take a dependency on the separate <c>FluentValidation.DependencyInjectionExtensions</c>
    /// package (see <c>SkathIO.Rogue.DiResolution.Tests/Fixtures.cs</c> for the same convention).
    /// <c>ValidationBehavior&lt;,&gt;</c> itself and <see cref="Behaviors.OrderAuditBehavior{TRequest,TResponse}"/>
    /// need no explicit registration — the generator auto-registers every behavior it discovers in
    /// this compilation (PD-17).</summary>
    public static IServiceCollection AddSmokeApplication(this IServiceCollection services)
    {
        services.AddScoped<IValidator<CreateOrderCommand>, CreateOrderCommandValidator>();
        return services;
    }
}
