using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SkathIO.Rogue.SourceGenerator;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// Verifies that editing unrelated source files does not re-run the generator's
/// expensive stages (NFR-MAINT-4 / incremental correctness).
/// </summary>
public sealed class IncrementalTests
{
    [Fact]
    public void UnrelatedEdit_DoesNotRetriggerGeneratorStages()
    {
        const string handlerSource = @"
using SkathIO.Rogue;
using System.Threading;
using System.Threading.Tasks;

public class Ping : ICommand<string> { }
public class PingHandler : ICommandHandler<Ping, string>
{
    public ValueTask<string> Handle(Ping request, CancellationToken cancellationToken)
        => new ValueTask<string>(""pong"");
}
";
        // Run 1: initial
        GeneratorTestHelper.RunGeneratorWithReason(
            handlerSource,
            out CSharpGeneratorDriver driver1,
            out CSharpCompilation compilation1);

        // Run 2: add an unrelated file (a plain utility class, no Rogue interfaces)
        const string unrelatedSource = @"
public static class StringHelper
{
    public static string Reverse(string s)
    {
        char[] arr = s.ToCharArray();
        System.Array.Reverse(arr);
        return new string(arr);
    }
}
";
        Microsoft.CodeAnalysis.SyntaxTree newTree = CSharpSyntaxTree.ParseText(unrelatedSource);
        CSharpCompilation compilation2 = compilation1.AddSyntaxTrees(newTree);
        driver1 = (CSharpGeneratorDriver)driver1.RunGenerators(compilation2);
        GeneratorDriverRunResult result2 = driver1.GetRunResult();

        // Generator should not have thrown
        Assert.All(result2.Results, static r => Assert.Null(r.Exception));

        // Step tracking is enabled in the test driver (GeneratorTestHelper.CreateDriver),
        // so tracked steps must be present and the assertion is unconditional.
        var allSteps = result2.Results
            .SelectMany(static r => r.TrackedSteps)
            .ToList();
        Assert.NotEmpty(allSteps);

        bool hasNonModified = allSteps.Any(static kvp =>
            kvp.Value.Any(static step =>
                step.Outputs.Any(static o =>
                    o.Reason != IncrementalStepRunReason.New &&
                    o.Reason != IncrementalStepRunReason.Modified)));

        Assert.True(hasNonModified,
            "Expected at least one generator step to be Cached or Unchanged after an " +
            "unrelated edit, but all steps were New or Modified. Check that model records " +
            "implement value equality correctly.");
    }

    [Fact]
    public void RepeatedRunWithUnchangedSource_DoesNotCrash()
    {
        const string source = @"
using SkathIO.Rogue;
using System.Threading;
using System.Threading.Tasks;

public class Query : ICommand<int> { }
public class QueryHandler : ICommandHandler<Query, int>
{
    public ValueTask<int> Handle(Query request, CancellationToken cancellationToken)
        => new ValueTask<int>(42);
}
";
        GeneratorTestHelper.RunGeneratorWithReason(
            source,
            out CSharpGeneratorDriver driver,
            out CSharpCompilation compilation);

        // Run the same compilation again — should not crash or produce a different result
        driver = (CSharpGeneratorDriver)driver.RunGenerators(compilation);
        GeneratorDriverRunResult result = driver.GetRunResult();

        Assert.All(result.Results, static r => Assert.Null(r.Exception));
    }

    // NFR-13 baseline (PD-44 amendment — moved to 11.2 so the D5 rework ships with its guard).
    // Counterpart to UnrelatedEdit_DoesNotRetriggerGeneratorStages: editing the MESSAGE/HANDLER source
    // (the discovery pipeline's actual input) must produce a re-run. Together these pin that the
    // re-keyed CQS discovery pipeline (equatable models, no ISymbol leak) caches on unrelated edits and
    // re-runs on relevant ones. They guard 11.3-11.5 as the dispatcher/adapter/analyzer reshapes land.
    [Fact]
    public void MessageHandlerEdit_RetriggersGeneratorStages()
    {
        const string handlerSource = @"
using SkathIO.Rogue;
using System.Threading;
using System.Threading.Tasks;

public class Ping : ICommand<string> { }
public class PingHandler : ICommandHandler<Ping, string>
{
    public ValueTask<string> Handle(Ping request, CancellationToken cancellationToken)
        => new ValueTask<string>(""pong"");
}
";
        // Run 1: initial single-handler source.
        GeneratorTestHelper.RunGeneratorWithReason(
            handlerSource,
            out CSharpGeneratorDriver driver1,
            out CSharpCompilation compilation1);

        // Run 2: edit the discovery input — add a SECOND command + handler. This changes the
        // discovered model set, so the pipeline must re-run (New/Modified outputs), not serve a stale
        // cache that would dispatch the old handler set.
        const string editedSource = @"
using SkathIO.Rogue;
using System.Threading;
using System.Threading.Tasks;

public class Ping : ICommand<string> { }
public class PingHandler : ICommandHandler<Ping, string>
{
    public ValueTask<string> Handle(Ping request, CancellationToken cancellationToken)
        => new ValueTask<string>(""pong"");
}

public class Pong : ICommand<string> { }
public class PongHandler : ICommandHandler<Pong, string>
{
    public ValueTask<string> Handle(Pong request, CancellationToken cancellationToken)
        => new ValueTask<string>(""ping"");
}
";
        // Replace the single syntax tree with the edited one (a genuine message/handler edit).
        SyntaxTree originalTree = compilation1.SyntaxTrees.Single();
        SyntaxTree editedTree = CSharpSyntaxTree.ParseText(editedSource);
        CSharpCompilation compilation2 = compilation1.ReplaceSyntaxTree(originalTree, editedTree);

        driver1 = (CSharpGeneratorDriver)driver1.RunGenerators(compilation2);
        GeneratorDriverRunResult result2 = driver1.GetRunResult();

        Assert.All(result2.Results, static r => Assert.Null(r.Exception));

        var allSteps = result2.Results
            .SelectMany(static r => r.TrackedSteps)
            .ToList();
        Assert.NotEmpty(allSteps);

        // A relevant edit must re-run the pipeline: at least one tracked step output is New or Modified.
        bool hasReRun = allSteps.Any(static kvp =>
            kvp.Value.Any(static step =>
                step.Outputs.Any(static o =>
                    o.Reason == IncrementalStepRunReason.New ||
                    o.Reason == IncrementalStepRunReason.Modified)));

        Assert.True(hasReRun,
            "Expected at least one generator step to be New or Modified after a message/handler edit, " +
            "but all steps were Cached/Unchanged — a stale-cache wrong-dispatch hazard (NFR-13).");

        // And the re-run must reflect the new model set: two handlers now discovered.
        DiscoveredModels models = RogueGenerator.ExtractFromCompilation(compilation2);
        Assert.Equal(2, models.Handlers.Count);
    }

    // NFR-13 closure / AC-J release-gate regression test (PD-44 amendment 2026-06-12).
    //
    // The declared release blocker is a STALE-CACHE WRONG-DISPATCH: if the incremental generator
    // served a cached dispatcher after the handler routing actually changed, the emitted switch would
    // route the OLD message — silently dispatching the wrong handler at runtime. The two baseline tests
    // above prove the pipeline caches on unrelated edits and re-runs when the discovered model set
    // grows. This test pins the worst case specifically: a handler that is RE-TARGETED from message A
    // to message B (a rename that leaves A unhandled) must (a) re-run the generator — never a cached
    // output — and (b) emit a dispatcher that routes B and contains NO stale A arm.
    [Fact]
    public void HandlerRetargetedToDifferentMessage_ReRunsAndEmitsNonStaleDispatcher()
    {
        // Run 1: a single query handler routing message Alpha.
        const string originalSource = @"
using SkathIO.Rogue;
using System.Threading;
using System.Threading.Tasks;

public class Alpha : IQuery<string> { }
public class Beta : IQuery<string> { }
public class TheHandler : IQueryHandler<Alpha, string>
{
    public ValueTask<string> Handle(Alpha request, CancellationToken cancellationToken)
        => new ValueTask<string>(""handled"");
}
";
        GeneratorDriverRunResult result1 = GeneratorTestHelper.RunGeneratorWithReason(
            originalSource,
            out CSharpGeneratorDriver driver1,
            out CSharpCompilation compilation1);

        Assert.All(result1.Results, static r => Assert.Null(r.Exception));

        // Sanity: the initial dispatcher routes Alpha (the case it would wrongly keep serving if stale).
        string dispatcher1 = GeneratedDispatcherSource(result1);
        Assert.Contains("Send_Alpha", dispatcher1);
        Assert.DoesNotContain("Send_Beta", dispatcher1);

        // Run 2: RE-TARGET the handler — it now handles Beta, leaving Alpha unhandled. This is the
        // exact edit a rename produces; a stale cache here is the wrong-dispatch release blocker.
        const string retargetedSource = @"
using SkathIO.Rogue;
using System.Threading;
using System.Threading.Tasks;

public class Alpha : IQuery<string> { }
public class Beta : IQuery<string> { }
public class TheHandler : IQueryHandler<Beta, string>
{
    public ValueTask<string> Handle(Beta request, CancellationToken cancellationToken)
        => new ValueTask<string>(""handled"");
}
";
        SyntaxTree originalTree = compilation1.SyntaxTrees.Single();
        SyntaxTree retargetedTree = CSharpSyntaxTree.ParseText(retargetedSource);
        CSharpCompilation compilation2 = compilation1.ReplaceSyntaxTree(originalTree, retargetedTree);

        driver1 = (CSharpGeneratorDriver)driver1.RunGenerators(compilation2);
        GeneratorDriverRunResult result2 = driver1.GetRunResult();

        Assert.All(result2.Results, static r => Assert.Null(r.Exception));

        // (a) The generator must NOT have served a stale cache for this re-targeting edit: at least one
        // tracked step output is New or Modified (i.e. its run reason is NOT Cached/Unchanged). A purely
        // cached run here is the wrong-dispatch hazard this test exists to forbid.
        var allSteps = result2.Results
            .SelectMany(static r => r.TrackedSteps)
            .ToList();
        Assert.NotEmpty(allSteps);

        bool reRan = allSteps.Any(static kvp =>
            kvp.Value.Any(static step =>
                step.Outputs.Any(static o =>
                    o.Reason == IncrementalStepRunReason.New ||
                    o.Reason == IncrementalStepRunReason.Modified)));

        Assert.True(reRan,
            "Generator served a fully-cached output after a handler was re-targeted from Alpha to Beta. " +
            "That is the stale-cache wrong-dispatch release blocker (NFR-13 / AC-J): the emitted switch " +
            "would still route Alpha to the now-Beta handler.");

        // (b) The re-emitted dispatcher routes Beta and carries NO stale Alpha arm. Asserting on the
        // emitted SOURCE (not just the discovered model) pins the actual wrong-dispatch surface.
        string dispatcher2 = GeneratedDispatcherSource(result2);
        Assert.Contains("Send_Beta", dispatcher2);
        Assert.DoesNotContain("Send_Alpha", dispatcher2);

        // And the discovered model agrees: the single handler now routes Beta, not Alpha.
        DiscoveredModels models = RogueGenerator.ExtractFromCompilation(compilation2);
        HandlerModel handler = Assert.Single(models.Handlers);
        Assert.EndsWith("Beta", handler.RequestFqn);
    }

    /// <summary>
    /// Concatenates every generated source for a run so a test can assert on the emitted dispatcher
    /// text (the actual wrong-dispatch surface), not just the discovered model.
    /// </summary>
    private static string GeneratedDispatcherSource(GeneratorDriverRunResult result)
        => string.Join(
            "\n",
            result.Results
                .SelectMany(static r => r.GeneratedSources)
                .Select(static s => s.SourceText.ToString()));
}
