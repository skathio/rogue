using Microsoft.Extensions.DependencyInjection;

namespace SkathIO.Rogue.Compatibility;

public static class MediatRCompatExtensions
{
    // Drop-in replacement for MediatR's AddMediatR(cfg => cfg.RegisterServicesFromAssembly(...)).
    // The SkathIO.Rogue source generator handles handler discovery at compile time;
    // the assembly-scan lambda is accepted but ignored (AOT-safe by design).
    //
    // A single nullable-default overload covers both the parameterless and the
    // configuration-lambda call shapes — two overloads differing only in the parameter's
    // nullability would be a duplicate signature (CS0111).
    public static IServiceCollection AddMediatR(
        this IServiceCollection services,
        System.Action<MediatRCompatOptions>? configure = null,
        System.Action<global::SkathIO.Rogue.RogueOptions>? rogueOptions = null)
        => services.AddRogue(rogueOptions);
}

// Placeholder config object — accepts the MediatR config lambda without error.
// Members are intentionally no-ops; the generator has already done the discovery.
public sealed class MediatRCompatOptions
{
    public MediatRCompatOptions RegisterServicesFromAssembly(System.Reflection.Assembly assembly) => this;
    public MediatRCompatOptions RegisterServicesFromAssemblyContaining<T>() => this;
    public MediatRCompatOptions RegisterServicesFromAssemblyContaining(System.Type type) => this;
    public MediatRCompatOptions RegisterServicesFromAssemblies(params System.Reflection.Assembly[] assemblies) => this;
}
