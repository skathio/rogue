using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SkathIO.Rogue.SourceGenerator;

/// <summary>
/// Wires the validation feature module into the generator. It does nothing unless the
/// SkathIO.Rogue.Validation package is referenced, so a compilation without it, or with only a marker
/// attribute of the same name, is generated exactly as before.
/// </summary>
internal static class ValidationModule
{
    internal const string HintName = "RogueValidation.g.cs";

    internal static IncrementalValueProvider<bool> RuntimeAvailable(IncrementalGeneratorInitializationContext context)
        => context.CompilationProvider.Select(static (compilation, _) => ValidationDiscovery.IsValidationRuntimeAvailable(compilation));

    internal static void Register(IncrementalGeneratorInitializationContext context, IncrementalValueProvider<DiscoveredModels> discovered)
    {
        IncrementalValueProvider<ImmutableArray<ValidatedTypeModel>> types = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                WellKnownTypeNames.ValidateAttribute,
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: static (ctx, ct) => ValidationDiscovery.ExtractValidatedType(ctx, ct))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!)
            .Collect();

        IncrementalValueProvider<ImmutableArray<AsyncRulesModel>> asyncRules = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is TypeDeclarationSyntax { BaseList.Types.Count: > 0 },
                transform: static (ctx, ct) => ValidationDiscovery.ExtractAsyncRules(ctx, ct))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!)
            .Collect();

        IncrementalValueProvider<ImmutableArray<string>> unmarkedOwners = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is PropertyDeclarationSyntax { AttributeLists.Count: > 0 },
                transform: static (ctx, ct) => ValidationDiscovery.ExtractUnmarkedRuleOwner(ctx, ct))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!)
            .Collect();

        // Reduced to a small equatable list, so the output re-runs only when the answer changes.
        IncrementalValueProvider<EquatableArray<string>> missingValidators = discovered
            .Combine(context.CompilationProvider)
            .Select(static (pair, ct) => pair.Left.ValidationRuntime
                ? ValidationDiscovery.FindRequestsWithoutValidator(pair.Left, pair.Right, ct)
                : EquatableArray<string>.Empty);

        IncrementalValueProvider<ValidationInputModel> input = types
            .Combine(asyncRules)
            .Combine(unmarkedOwners)
            .Combine(missingValidators)
            .Select(static (pair, _) => new ValidationInputModel(
                EquatableArray<ValidatedTypeModel>.From(pair.Left.Left.Left),
                EquatableArray<AsyncRulesModel>.From(pair.Left.Left.Right),
                Distinct(pair.Left.Right),
                pair.Right));

        context.RegisterSourceOutput(input.Combine(discovered), static (spc, pair) => Emit(spc, pair.Left, pair.Right));
    }

    private static void Emit(SourceProductionContext spc, ValidationInputModel input, DiscoveredModels models)
    {
        if (!models.ValidationRuntime)
        {
            return;
        }

        ReportDiagnostics(spc, input);
        spc.AddSource(HintName, SourceText.From(CodeWriter.Header + ValidationEmitter.Emit(input, models), Encoding.UTF8));
    }

    private static void ReportDiagnostics(SourceProductionContext spc, ValidationInputModel input)
    {
        foreach (ValidatedTypeModel type in input.Types)
        {
            foreach (ValidationDiagnosticModel diagnostic in type.Diagnostics)
            {
                Report(spc, diagnostic.Id, ToArray(diagnostic.Arguments));
            }
        }
        foreach (string request in input.RequestsWithoutValidator)
        {
            Report(spc, "ROGUE017", request, request);
        }
        foreach (string owner in input.UnmarkedRuleOwners)
        {
            Report(spc, "ROGUE018", owner);
        }
        foreach (AsyncRulesModel rules in input.AsyncRules)
        {
            if (!rules.RequestIsValidated)
            {
                Report(spc, "ROGUE018", rules.RequestFqn.Replace("global::", string.Empty));
            }
        }
    }

    private static void Report(SourceProductionContext spc, string id, params string[] arguments)
    {
        DiagnosticDescriptor descriptor = id switch
        {
            "ROGUE014" => DiagnosticDescriptors.ValidatedTypeNotPartial,
            "ROGUE015" => DiagnosticDescriptors.RuleDoesNotFitProperty,
            "ROGUE016" => DiagnosticDescriptors.RulesHookSignature,
            "ROGUE017" => DiagnosticDescriptors.ValidatorMissing,
            _ => DiagnosticDescriptors.RulesNeverRun,
        };
        spc.ReportDiagnostic(Diagnostic.Create(descriptor, Location.None, arguments));
    }

    private static string[] ToArray(EquatableArray<string> values)
    {
        var array = new string[values.Count];
        for (int i = 0; i < array.Length; i++)
        {
            array[i] = values[i];
        }
        return array;
    }

    private static EquatableArray<string> Distinct(ImmutableArray<string> values)
    {
        var seen = new HashSet<string>();
        var distinct = ImmutableArray.CreateBuilder<string>();
        foreach (string value in values)
        {
            if (seen.Add(value))
            {
                distinct.Add(value);
            }
        }
        return EquatableArray<string>.From(distinct.ToImmutable());
    }
}
