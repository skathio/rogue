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
using Microsoft.CodeAnalysis.Diagnostics;
using SkathIO.Rogue;
using SkathIO.Rogue.Compatibility;
using SkathIO.Rogue.Migration.Analyzer;
using Xunit;

namespace SkathIO.Rogue.Migration.Tests;

/// <summary>
/// AC-F gate: the real ~50-handler MediatR migration sample is mechanically migrated by the
/// analyzer code-fixes (ROGM001 + ROGM002), recompiled against the real SkathIO.Rogue assemblies,
/// and its entry point is run — all within the SRS 15-minute ceiling. See PD-32 / PD-32a.
/// </summary>
public sealed class MigrationGateTests
{
    [Fact]
    public async Task AC_F_MigrationGate_AppliesAllFixes_RecompilesAndRuns_WithinTimeCeiling()
    {
        var stopwatch = Stopwatch.StartNew();

        var sampleDir = ResolveSampleDirectory();

        // Step 1 — mechanically apply every migration code-fix to a fixed point.
        var fixedSources = await AnalyzerTestHelper.ApplyAllFixesAsync(
            sampleDir,
            new DiagnosticAnalyzer[] { new UsingMediatRAnalyzer(), new TaskReturnTypeAnalyzer() },
            new CodeFixProvider[] { new ReplaceUsingMediatRCodeFix(), new ReplaceTaskReturnTypeCodeFix() });

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
