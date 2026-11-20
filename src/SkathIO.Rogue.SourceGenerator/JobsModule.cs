using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace SkathIO.Rogue.SourceGenerator;

/// <summary>
/// Wires the jobs feature module into the generator. When the SkathIO.Rogue.Jobs package is referenced
/// it emits, into its own file, one job definition per command: the stable kind, the <c>[Job]</c>
/// defaults and a typed invoker that sends the command through the dispatcher. A job is looked up by
/// that kind, never by a type name, and nothing here touches the dispatcher, so every request's
/// <c>Send_X</c> is generated exactly as without the package.
/// </summary>
internal static class JobsModule
{
    internal const string HintName = "RogueJobs.g.cs";

    internal static void Register(IncrementalGeneratorInitializationContext context, IncrementalValueProvider<DiscoveredModels> discovered)
    {
        IncrementalValueProvider<bool> runtime = context.CompilationProvider
            .Select(static (compilation, _) => compilation.GetTypeByMetadataName(WellKnownTypeNames.JobDefinition1) is not null);

        context.RegisterSourceOutput(discovered.Combine(runtime), static (spc, pair) =>
        {
            if (!pair.Right)
            {
                return;
            }
            List<HandlerModel> jobs = Jobs(pair.Left.Handlers);
            if (jobs.Count > 0)
            {
                spc.AddSource(HintName, SourceText.From(CodeWriter.Header + Emit(jobs), Encoding.UTF8));
            }
        });
    }

    /// <summary>
    /// Reads the named arguments of a command type's <c>[Job]</c> attribute, or returns null when it has
    /// none. An attribute that does not resolve (error type) is ignored, as for the module markers.
    /// </summary>
    internal static JobOptionsModel? ReadOptions(ITypeSymbol requestType)
    {
        foreach (AttributeData attr in requestType.GetAttributes())
        {
            if (IsJobAttribute(attr.AttributeClass))
            {
                return ReadOptions(attr.NamedArguments);
            }
        }
        return null;
    }

    // JobAttribute is a top-level, non-generic type, so its namespace and name make its metadata name.
    private static bool IsJobAttribute(INamedTypeSymbol? attributeClass)
        => attributeClass is { TypeKind: not TypeKind.Error, ContainingType: null }
            && attributeClass.ContainingNamespace.ToDisplayString() + "." + attributeClass.MetadataName == WellKnownTypeNames.JobAttribute;

    private static JobOptionsModel ReadOptions(ImmutableArray<KeyValuePair<string, TypedConstant>> arguments)
    {
        string? kind = null;
        string? queue = null;
        int maxAttempts = 0;
        int backoffSeconds = 0;
        int timeoutSeconds = 0;
        foreach (KeyValuePair<string, TypedConstant> argument in arguments)
        {
            switch (argument.Key)
            {
                case "Kind": kind = argument.Value.Value as string; break;
                case "Queue": queue = argument.Value.Value as string; break;
                case "MaxAttempts": maxAttempts = AsInt(argument.Value); break;
                case "BackoffSeconds": backoffSeconds = AsInt(argument.Value); break;
                case "TimeoutSeconds": timeoutSeconds = AsInt(argument.Value); break;
            }
        }
        return new JobOptionsModel(kind, queue, maxAttempts, backoffSeconds, timeoutSeconds);
    }

    private static int AsInt(TypedConstant value) => value.Value is int number ? number : 0;

    // Every command whose handler the dispatcher can reach from generated code, once per command type.
    private static List<HandlerModel> Jobs(EquatableArray<HandlerModel> handlers)
    {
        var jobs = new List<HandlerModel>();
        var seen = new HashSet<string>();
        foreach (HandlerModel handler in handlers)
        {
            if (handler.Kind == HandlerKind.Command
                && !handler.IsAdapterMapped
                && !handler.IsAbstract
                && handler.RequestAccessibility != TypeAccessibility.Other
                && handler.ResponseAccessibility != TypeAccessibility.Other
                && seen.Add(handler.RequestFqn))
            {
                jobs.Add(handler);
            }
        }
        return jobs;
    }

    private static string Emit(List<HandlerModel> jobs)
    {
        var w = new CodeWriter();
        FeatureModules.OpenRegistrar(w, "RogueJobsRegistration");
        foreach (HandlerModel job in jobs)
        {
            EmitDefinition(w, job);
        }
        FeatureModules.CloseRegistrar(w);
        return w.ToString();
    }

    private static void EmitDefinition(CodeWriter w, HandlerModel job)
    {
        string requestFqn = DispatcherEmitter.ToGlobalFqn(job.RequestFqn);
        JobOptionsModel? options = job.JobOptions;

        // The default kind is the command's full name without spaces (generic arguments keep their commas).
        string kind = options?.Kind ?? job.RequestFqn.Replace(" ", string.Empty);
        string defaults = options is null
            ? "global::SkathIO.Rogue.Jobs.JobDefaults.None"
            : "new global::SkathIO.Rogue.Jobs.JobDefaults(" + Literal(options.Queue) + ", " + options.MaxAttempts + ", " + options.BackoffSeconds + ", " + options.TimeoutSeconds + ")";
        string send = job.ResponseFqn is null
            ? "dispatcher.Send((global::SkathIO.Rogue.ICommand)command, cancellationToken)"
            : "global::SkathIO.Rogue.Jobs.JobDefinition.IgnoreResult(dispatcher.Send<" + DispatcherEmitter.ToGlobalFqn(job.ResponseFqn) + ">((global::SkathIO.Rogue.ICommand<" + DispatcherEmitter.ToGlobalFqn(job.ResponseFqn) + ">)command, cancellationToken))";

        w.Line("global::SkathIO.Rogue.Jobs.JobsServiceCollectionExtensions.AddJobDefinition(services, new global::SkathIO.Rogue.Jobs.JobDefinition<" + requestFqn + ">(");
        w.Line("    " + Literal(kind) + ",");
        w.Line("    " + defaults + ",");
        w.Line("    static (dispatcher, command, cancellationToken) => " + send + "));");
    }

    private static string Literal(string? value)
        => value is null ? "null" : SymbolDisplay.FormatLiteral(value, quote: true);
}
