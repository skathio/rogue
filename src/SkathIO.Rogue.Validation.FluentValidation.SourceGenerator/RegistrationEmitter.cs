namespace SkathIO.Rogue.Validation.FluentValidation.SourceGenerator;

/// <summary>
/// Emits the standalone registration class <c>RogueFluentValidationRegistration</c> in the
/// <c>SkathIO.Rogue.Validation.FluentValidation.Generated</c> namespace. Its <c>Register</c> method
/// registers every discovered validator into DI. Mirrors
/// <c>SkathIO.Rogue.SourceGenerator/RegistrationEmitter.cs</c>'s shape, scoped to validators only —
/// see D1 for why this project does not reference (and cannot reuse types from) that project.
/// This class is ALWAYS emitted (even with an empty body for zero-validator compilations), mirroring
/// the core generator's own <c>RogueGeneratedRegistration</c> — the append-only
/// <see cref="global::SkathIO.Rogue.RogueRegistrationBridge"/> only ever gains a registrar when
/// <see cref="EmitModuleInit"/> decides there is something to append (see that method's remarks).
/// Output: <c>RogueFluentValidationServiceCollectionExtensions.g.cs</c>.
/// </summary>
internal static class RegistrationEmitter
{
    // The generated file carries no `using` directives (mirrors the core generator's emitted
    // style), so every call is fully qualified through its declaring static class.
    private const string SCDE_FQN =
        "global::Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions";

    /// <summary>
    /// D4: discovered validator registrations are hard-pinned to Scoped, decoupled from
    /// <c>RogueOptions.Lifetime</c> — the same treatment <c>fluentvalidation-di-fix</c> D2/D3 already
    /// gave pipeline behaviors, for the identical captive-dependency reason (a validator with a
    /// Scoped dependency, e.g. a <c>DbContext</c> uniqueness check, must never become captive under a
    /// Singleton-configured host). Never <c>options.Lifetime</c>.
    /// </summary>
    private const string ScopedLifetimeFqn =
        "global::Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped";

    internal static string Emit(DiscoveredValidators models)
    {
        var w = new CodeWriter();

        w.Line("namespace SkathIO.Rogue.Validation.FluentValidation.Generated");
        w.Line("{");
        w.Indent();

        w.Open("internal static class RogueFluentValidationRegistration");
        w.Open("internal static void Register(global::Microsoft.Extensions.DependencyInjection.IServiceCollection services, global::SkathIO.Rogue.RogueOptions options)");

        // R5/D4: TryAddEnumerable (not TryAdd) — ValidationBehavior.cs consumes
        // IEnumerable<IValidator<TRequest>>, so two validators for the same request type is a
        // legitimate, supported shape (e.g. rules split across two validator classes). Plain TryAdd
        // would keep only the first and silently drop the rest. TryAddEnumerable also dedups by
        // implementation type, so re-invoking the registrar (idempotent, mirrors PD-38) does not add
        // the same validator twice.
        foreach (ValidatorModel validator in models.Validators)
        {
            string requestFqn   = ToGlobalFqn(validator.RequestFqn);
            string validatorFqn = ToGlobalFqn(validator.TypeFqn);
            string iface        = "global::FluentValidation.IValidator<" + requestFqn + ">";

            w.Line(
                SCDE_FQN + ".TryAddEnumerable(services, global::Microsoft.Extensions.DependencyInjection.ServiceDescriptor.Describe(" +
                "typeof(" + iface + "), typeof(" + validatorFqn + "), " + ScopedLifetimeFqn + "));");
        }

        w.Close(); // Register
        w.Close(); // class RogueFluentValidationRegistration

        w.Dedent();
        w.Line("}"); // namespace

        return w.ToString();
    }

    /// <summary>
    /// Returns <c>true</c> when the compilation discovered zero validators. Simpler than the core
    /// generator's <c>HasNothingToRegister</c> (<c>RegistrationEmitter.cs:163-177</c>) since D2's
    /// source-only design has no <c>IsMetadata</c> case to guard against at all — there is no
    /// metadata-discovered validator that could wrongly count as "something to register" here,
    /// because nothing is ever discovered via metadata in the first place.
    /// </summary>
    internal static bool HasNothingToRegister(DiscoveredValidators models) => models.Validators.Count == 0;

    /// <summary>
    /// Emits a module initializer (net5+) that appends this compilation's
    /// <c>RogueFluentValidationRegistration.Register</c> to the DLL's append-only
    /// <c>RogueRegistrationBridge</c> registry — a SECOND, independent registrar appended to the same
    /// public, append-only bridge the core generator's own module initializer also appends to. The
    /// bridge is documented to run <em>every</em> registered registrar (not just one,
    /// <c>RogueRegistrationBridge.cs:16-24</c>), so two independent generators both appending to it is
    /// a supported, safe shape — confirmed by Iteration 1.1's review.
    /// <para>
    /// Suppressed (empty <c>#if !NETSTANDARD2_0 #endif</c> shell) when
    /// <see cref="HasNothingToRegister"/> is true — mirrors the core generator's PD-45 suppression
    /// intent (<c>RegistrationEmitter.cs:206-212</c>), simplified per D2: a zero-validator compilation
    /// has nothing to contribute, so no empty registrar is appended. On ns2.0 (no
    /// <c>ModuleInitializer</c>) there is no explicit fallback call here (unlike the core generator) —
    /// D5 removed the only call that could have invoked one; a ns2.0 consumer's validators are
    /// discovered by referencing the package the same as any other TFM, but registration itself
    /// requires a net5+ host process for the module initializer to run.
    /// </para>
    /// Output: <c>RogueFluentValidationModuleInit.g.cs</c> (empty when suppressed).
    /// </summary>
    internal static string EmitModuleInit(DiscoveredValidators models)
    {
        var w = new CodeWriter();
        w.Line("#if !NETSTANDARD2_0");

        if (HasNothingToRegister(models))
        {
            w.Line("#endif");
            return w.ToString();
        }

        w.Line("namespace SkathIO.Rogue.Validation.FluentValidation.Generated");
        w.Line("{");
        w.Indent();
        w.Open("internal static class RogueFluentValidationModuleInit");
        w.Line("[global::System.Runtime.CompilerServices.ModuleInitializer]");
        w.Open("internal static void Init()");
        w.Line("global::SkathIO.Rogue.RogueRegistrationBridge.Register(");
        w.Line("    (svc, opts) => global::SkathIO.Rogue.Validation.FluentValidation.Generated.RogueFluentValidationRegistration.Register(svc, opts));");
        w.Close(); // Init
        w.Close(); // class RogueFluentValidationModuleInit
        w.Dedent();
        w.Line("}");
        w.Line("#endif");
        return w.ToString();
    }

    /// <summary>
    /// Duplicated from <c>DispatcherEmitter.ToGlobalFqn</c> (this project cannot reference the core
    /// generator project — see D1/<see cref="CodeWriter"/>'s remarks for why). Replaces keyword type
    /// aliases with their CLR names so e.g. <c>"string"</c> becomes <c>"global::System.String"</c>
    /// instead of the invalid <c>"global::string"</c> — <c>ValidatorModel.RequestFqn</c>/<c>TypeFqn</c>
    /// are produced via <c>SymbolDisplayFormat.FullyQualifiedFormat</c>, which renders primitive types
    /// using their C# keyword form.
    /// </summary>
    private static string ToGlobalFqn(string fqn)
    {
        switch (fqn)
        {
            case "string":  return "global::System.String";
            case "int":     return "global::System.Int32";
            case "long":    return "global::System.Int64";
            case "bool":    return "global::System.Boolean";
            case "double":  return "global::System.Double";
            case "float":   return "global::System.Single";
            case "decimal": return "global::System.Decimal";
            case "byte":    return "global::System.Byte";
            case "short":   return "global::System.Int16";
            case "char":    return "global::System.Char";
            case "object":  return "global::System.Object";
            case "uint":    return "global::System.UInt32";
            case "ulong":   return "global::System.UInt64";
            case "ushort":  return "global::System.UInt16";
            case "sbyte":   return "global::System.SByte";
            default:        return "global::" + fqn;
        }
    }
}
