using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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

public class Ping : IRequest<string> { }
public class PingHandler : IRequestHandler<Ping, string>
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

public class Query : IRequest<int> { }
public class QueryHandler : IRequestHandler<Query, int>
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
}
