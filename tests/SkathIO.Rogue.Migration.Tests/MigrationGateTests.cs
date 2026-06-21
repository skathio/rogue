using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using SkathIO.Rogue;
using SkathIO.Rogue.Compatibility;
using SkathIO.Rogue.Migration.Analyzer;
using Xunit;

namespace SkathIO.Rogue.Migration.Tests;

/// <summary>
/// AC-F gate: the real ~50-handler MediatR migration sample is mechanically migrated by the
/// analyzer code-fixes (ROGM001 using-rewrite + ROGM002 Task→ValueTask + ROGM006 marker/handler
/// rewrite onto the CQS contracts), recompiled against the real SkathIO.Rogue assemblies, and its
/// entry point is run — all within the SRS 15-minute ceiling. The migration target is the post-D5
/// CQS core (ICommand/IQuery/IEvent + handlers), not the adapter IRequest shapes (FR-13/PD-43/PD-44).
/// See PD-32 / PD-32a.
/// </summary>
public sealed class MigrationGateTests
{
    [Fact]
    public async Task AC_F_MigrationGate_AppliesAllFixes_RecompilesAndRuns_WithinTimeCeiling()
    {
        var stopwatch = Stopwatch.StartNew();

        var sampleDir = ResolveSampleDirectory();

        // Step 1 — mechanically apply every migration code-fix to a fixed point. ROGM006 rewrites the
        // MediatR marker/handler base lists onto the CQS contracts; ROGM002 rewrites Task→ValueTask;
        // ROGM001 rewrites the `using MediatR;` directive. (ROGM003/ROGM005 are diagnostic-only — no
        // code-fix — so they do not drive the fixed-point loop.)
        var fixedSources = await AnalyzerTestHelper.ApplyAllFixesAsync(
            sampleDir,
            new DiagnosticAnalyzer[]
            {
                new UsingMediatRAnalyzer(),
                new TaskReturnTypeAnalyzer(),
                new MediatRMarkerTypeAnalyzer(),
            },
            new CodeFixProvider[]
            {
                new ReplaceUsingMediatRCodeFix(),
                new ReplaceTaskReturnTypeCodeFix(),
                new MigrateMediatRMarkerTypeCodeFix(),
            });

        // The migration must have removed every `using MediatR;` directive (ROGM001). Check for the
        // directive at the start of a line (a trimmed line equal to it) so a prose mention of the
        // directive inside a code comment does not trip the assertion. MediatRStubs.cs has no such
        // directive (it declares the stub namespace), so it is exempt by construction.
        foreach (var (name, source) in fixedSources)
        {
            var hasUsingMediatRDirective = source
                .Split('\n')
                .Any(line => line.TrimEnd('\r').Trim() == "using MediatR;");

            Assert.False(
                hasUsingMediatRDirective,
                $"ROGM001 left a `using MediatR;` directive in {name} after migration.");
        }

        // The migration must have rewritten every MediatR marker/handler base reference onto the CQS
        // contracts (ROGM006). No type may still declare `: IRequest`/`IRequestHandler`/`INotification`/
        // `INotificationHandler`/`IStreamRequest`/`IStreamRequestHandler` in its base list. The check
        // inspects the parsed syntax tree's base lists (not raw text), so a prose mention of a MediatR
        // name inside a comment does not trip it. MediatRStubs.cs *declares* the MediatR stub interfaces
        // (their definitions, not implementations of them) and is exempt by construction.
        foreach (var (name, source) in fixedSources)
        {
            if (name == "MediatRStubs.cs")
            {
                continue;
            }

            var survivor = FindSurvivingMediatRBaseType(source);
            Assert.True(
                survivor is null,
                $"ROGM006 left a `{survivor}` base-list reference in {name} after migration.");
        }

        // Step 2 — recompile the migrated source against the real Rogue references and run it.
        var references = BuildReferenceSet();
        var (zeroErrors, errors, recompileMs) =
            AnalyzerTestHelper.RecompileAndRun(fixedSources, references);

        stopwatch.Stop();

        Assert.True(
            zeroErrors,
            $"Migrated sample failed to recompile against Rogue ({errors.Length} error(s)): " +
            string.Join("; ", errors.Select(e => e.ToString())));

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMinutes(15),
            $"AC-F gate exceeded the 15-minute ceiling: {stopwatch.Elapsed.TotalSeconds:F1}s " +
            $"(recompile {recompileMs} ms).");
    }

    /// <summary>
    /// The MediatR marker/handler interface names ROGM006 must eliminate from base lists. A migrated
    /// type lands on the CQS contracts (<c>ICommand</c>/<c>IQuery</c>/<c>IEvent</c> + handlers), none of
    /// which is in this set.
    /// </summary>
    private static readonly HashSet<string> MediatRBaseMarkers = new(System.StringComparer.Ordinal)
    {
        "IRequest",
        "IRequestHandler",
        "INotification",
        "INotificationHandler",
        "IStreamRequest",
        "IStreamRequestHandler",
    };

    /// <summary>
    /// Returns the simple name of the first MediatR marker/handler interface still present in any base
    /// list of <paramref name="source"/>, or <c>null</c> when none survives. Inspects the parsed syntax
    /// tree (not raw text) so a comment that names a MediatR type is not a false positive. The
    /// authoritative correctness gate is the Step-2 recompile against the reshaped core (a surviving
    /// MediatR base would fail with CS0246); this assertion is a fast, readable belt-and-suspenders check.
    /// </summary>
    private static string? FindSurvivingMediatRBaseType(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            if (type.BaseList is null)
            {
                continue;
            }

            foreach (var baseType in type.BaseList.Types)
            {
                var name = GetSimpleBaseName(baseType.Type);
                if (name is not null && MediatRBaseMarkers.Contains(name))
                {
                    return name;
                }
            }
        }

        return null;
    }

    private static string? GetSimpleBaseName(TypeSyntax type)
    {
        switch (type)
        {
            case IdentifierNameSyntax id:
                return id.Identifier.Text;
            case GenericNameSyntax g:
                return g.Identifier.Text;
            case QualifiedNameSyntax q:
                return GetSimpleBaseName(q.Right);
            default:
                return null;
        }
    }

    private static string ResolveSampleDirectory()
    {
        var dir = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
            "..", "..", "..", "..", "..", "samples", "mediatr-migration", "before"));

        Assert.True(Directory.Exists(dir), $"Sample directory not found: {dir}");
        return dir;
    }

    private static IReadOnlyList<MetadataReference> BuildReferenceSet()
    {
        // typeof(ISender) -> SkathIO.Rogue.Abstractions (markers + handlers + Unit live here);
        // typeof(MediatRCompatExtensions) -> SkathIO.Rogue.MediatR (compat shim).
        var refs = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(ISender).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(MediatRCompatExtensions).Assembly.Location),
        };

        // The shared framework assemblies the migrated sample needs (records, Console, ValueTask,
        // collections). Pulling them from the trusted-platform-assemblies set keeps this robust
        // across the net10.0 reference layout without hard-coding paths.
        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var path in tpa)
        {
            refs.Add(MetadataReference.CreateFromFile(path));
        }

        return refs;
    }
}
