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
    internal static readonly FeatureModule[] All =
    {
        new FeatureModule("Validation", WellKnownTypeNames.ValidateAttribute, "SkathIO.Rogue.Validation"),
        new FeatureModule("Jobs", WellKnownTypeNames.JobAttribute, "SkathIO.Rogue.Jobs"),
        new FeatureModule("Messaging", WellKnownTypeNames.MessageAttribute, "SkathIO.Rogue.Messaging"),
    };

    /// <summary>
    /// Name of the pre-handler hook a feature module implements for one request, for example
    /// <c>PreHandle_Validation_Send_App_CreateOrder</c>.
    /// </summary>
    internal static string PreHandlerHookName(string module, string sendMethodName)
        => "PreHandle_" + module + "_" + sendMethodName;

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

    internal static void EmitPreHandlerSteps(CodeWriter w, HandlerModel handler, string methodName)
    {
        foreach (string module in handler.MarkedModules)
        {
            w.Line(PreHandlerHookName(module, methodName) + "(request, cancellationToken);");
        }
    }
}
