namespace SkathIO.Rogue.SourceGenerator;

/// <summary>
/// A feature package whose code generation lives in this generator. The module is on for a
/// request when the request carries the module's marker attribute and that attribute resolves to
/// the real marker type, so a compilation without the feature package never sees the module.
/// </summary>
internal sealed record FeatureModule(
    /// <summary>Short module name, used in generated member names (for example <c>Validation</c>).</summary>
    string Name,
    /// <summary>Metadata name of the marker attribute that switches the module on.</summary>
    string MarkerMetadataName,
    /// <summary>Assembly (and package) name whose version must match the core package's major.minor.</summary>
    string PackageAssemblyName);

/// <summary>
/// The feature modules the core generator knows. A feature package's generated code is produced
/// here rather than by a generator of its own, because a separate generator cannot add a step to
/// the typed <c>Send_X</c> the core generator emits; the price is that core and feature package
/// versions are coupled, which ROGUE013 reports.
/// </summary>
internal static class FeatureModules
{
    internal const string ValidationModule = "Validation";

    internal static readonly FeatureModule[] All =
    {
        new FeatureModule(ValidationModule, WellKnownTypeNames.ValidateAttribute, "SkathIO.Rogue.Validation"),
        new FeatureModule("Jobs", WellKnownTypeNames.JobAttribute, "SkathIO.Rogue.Jobs"),
        new FeatureModule("Messaging", WellKnownTypeNames.MessageAttribute, "SkathIO.Rogue.Messaging"),
    };

    /// <summary>
    /// Name of the pre-handler hook a feature module implements for one request, for example
    /// <c>PreHandle_Validation_Send_App_CreateOrder</c>.
    /// </summary>
    internal static string PreHandlerHookName(string module, string sendMethodName)
        => "PreHandle_" + module + "_" + sendMethodName;

    internal static bool Marks(HandlerModel handler, string module)
    {
        foreach (string marked in handler.MarkedModules)
        {
            if (marked == module)
            {
                return true;
            }
        }
        return false;
    }

    internal static bool HasMarkedRequest(DiscoveredModels models)
    {
        foreach (var handler in models.Handlers)
        {
            if (handler.MarkedModules.Count > 0)
            {
                return true;
            }
        }
        return false;
    }

    internal static void EmitPreHandlerHookDeclarations(CodeWriter w, HandlerModel handler, string methodName, string requestFqn)
    {
        foreach (string module in handler.MarkedModules)
        {
            w.Line("partial void " + PreHandlerHookName(module, methodName) + "(" + requestFqn + " request, global::System.Threading.CancellationToken cancellationToken);");
        }
    }

    /// <summary>
    /// Emits the pre-handler steps at the top of a marked request's <c>Send_X</c>: one hook call per
    /// module, then, for a validated request when the validation package is referenced, the
    /// <c>IAsyncRules&lt;T&gt;</c> step. That step can complete asynchronously, so it splits the method:
    /// <c>Send_X</c> ends by calling <c>Send_X_Handle</c>, which this method opens and whose body (the
    /// unchanged handler dispatch) the caller goes on to write.
    /// </summary>
    internal static void EmitPreHandlerSteps(CodeWriter w, HandlerModel handler, string methodName, bool validationRuntime)
    {
        foreach (string module in handler.MarkedModules)
        {
            w.Line(PreHandlerHookName(module, methodName) + "(request, cancellationToken);");
        }

        if (validationRuntime && Marks(handler, ValidationModule))
        {
            EmitAsyncRulesStep(w, handler, methodName);
        }
    }

    /// <summary>
    /// Opens a feature module's registrar: a class in <c>SkathIO.Rogue.Generated</c> whose module
    /// initializer appends its <c>Register(services)</c> method to the registration bridge, which
    /// <c>AddRogue</c> runs. Leaves <c>Register</c> open for the caller's registrations; close it with
    /// <see cref="CloseRegistrar"/>.
    /// </summary>
    internal static void OpenRegistrar(CodeWriter w, string className)
    {
        w.Open("namespace SkathIO.Rogue.Generated");
        w.Open("internal static class " + className);
        w.Line("[global::System.Runtime.CompilerServices.ModuleInitializer]");
        w.Open("internal static void Init()");
        w.Line("global::SkathIO.Rogue.RogueRegistrationBridge.Register(static (services, options) => Register(services));");
        w.Close();
        w.Line();
        w.Open("private static void Register(global::Microsoft.Extensions.DependencyInjection.IServiceCollection services)");
    }

    /// <summary>Closes what <see cref="OpenRegistrar"/> opened: the method, the class and the namespace.</summary>
    internal static void CloseRegistrar(CodeWriter w)
    {
        w.Close();
        w.Close();
        w.Close();
    }

    private static void EmitAsyncRulesStep(CodeWriter w, HandlerModel handler, string methodName)
    {
        string requestFqn = DispatcherEmitter.ToGlobalFqn(handler.RequestFqn);
        string responseFqn = handler.ResponseFqn is null ? "global::SkathIO.Rogue.Unit" : DispatcherEmitter.ToGlobalFqn(handler.ResponseFqn);
        string valueTask = "global::System.Threading.Tasks.ValueTask<" + responseFqn + ">";
        string parameters = "(" + requestFqn + " request, global::System.Threading.CancellationToken cancellationToken)";

        // With no IAsyncRules<T> registered, DI hands back its cached empty array and the runner returns
        // a completed task, so the valid path stays synchronous and allocation-free.
        w.Line("var asyncRules = global::SkathIO.Rogue.Validation.AsyncRulesRunner.CheckAsync(");
        w.Line("    global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetService<global::System.Collections.Generic.IEnumerable<global::SkathIO.Rogue.Validation.IAsyncRules<" + requestFqn + ">>>(_serviceProvider),");
        w.Line("    request, cancellationToken);");
        w.Open("if (!asyncRules.IsCompletedSuccessfully)");
        w.Line("return " + methodName + "_AfterAsyncRules(asyncRules, request, cancellationToken);");
        w.Close();
        w.Line("asyncRules.GetAwaiter().GetResult();");
        w.Line("return " + methodName + "_Handle(request, cancellationToken);");
        w.Close();
        w.Line();

        w.Open("private async " + valueTask + " " + methodName + "_AfterAsyncRules(global::System.Threading.Tasks.ValueTask asyncRules, " + parameters.Substring(1));
        w.Line("await asyncRules.ConfigureAwait(false);");
        w.Line("return await " + methodName + "_Handle(request, cancellationToken).ConfigureAwait(false);");
        w.Close();
        w.Line();

        w.Open("private " + valueTask + " " + methodName + "_Handle" + parameters);
    }
}
