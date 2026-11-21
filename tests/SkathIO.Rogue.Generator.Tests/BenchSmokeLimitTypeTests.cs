using System.Text.Json.Nodes;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// A limit in .bench/thresholds.json that is not a number must fail the CI benchmark smoke that reads
/// it. jq orders every number before every string, so a measured allocation compared with a limit
/// written as text is never over it, and the gate would pass whatever was measured.
/// </summary>
public sealed class BenchSmokeLimitTypeTests
{
    private const string JobsBench = "bench/SkathIO.Rogue.Benchmarks.Jobs";
    private const string ValidationBench = "bench/SkathIO.Rogue.Benchmarks.Validation";
    private const string JobsLimit = "jobs_rogue_bytes_per_job_ceiling";
    private const string ValidationLimits = "validation_rogue_bytes_ceiling";

    [Fact]
    public void TheJobsSmoke_Fails_WhenItsLimitIsTextInsteadOfANumber()
    {
        JsonObject thresholds = BenchSmokeRulesTests.Thresholds();
        thresholds[JobsLimit] = "4096";

        SmokeOutcome outcome = SmokeRun.Execute(JobsBench, static bytes => bytes, thresholds);

        Assert.True(outcome.Staged > 0, "The jobs smoke selects no benchmark that has committed results.");
        Assert.True(outcome.ExitCode != 0, "The jobs smoke passes with " + JobsLimit + " written as text, so it gates on nothing:\n" + outcome.Output);
    }

    [Fact]
    public void TheJobsSmoke_Fails_WhenItsLimitIsLeftOut()
    {
        JsonObject thresholds = BenchSmokeRulesTests.Thresholds();
        thresholds.Remove(JobsLimit);

        SmokeOutcome outcome = SmokeRun.Execute(JobsBench, static bytes => bytes, thresholds);

        Assert.True(outcome.ExitCode != 0, "The jobs smoke passes with no " + JobsLimit + " to gate on:\n" + outcome.Output);
    }

    [Fact]
    public void TheValidationSmoke_Fails_WhenOneOfItsLimitsIsTextInsteadOfANumber()
    {
        JsonObject thresholds = BenchSmokeRulesTests.Thresholds();
        thresholds[ValidationLimits]!["RogueValidation_Invalid"] = "2048";

        SmokeOutcome outcome = SmokeRun.Execute(ValidationBench, static bytes => bytes, thresholds);

        Assert.True(outcome.Staged > 0, "The validation smoke selects no benchmark that has committed results.");
        Assert.True(outcome.ExitCode != 0, "The validation smoke passes with a limit written as text, so it does not gate on it:\n" + outcome.Output);
    }

    [Fact]
    public void TheValidationSmoke_Fails_WhenItsLimitsAreLeftOut()
    {
        JsonObject thresholds = BenchSmokeRulesTests.Thresholds();
        thresholds.Remove(ValidationLimits);

        SmokeOutcome outcome = SmokeRun.Execute(ValidationBench, static bytes => bytes, thresholds);

        Assert.True(outcome.ExitCode != 0, "The validation smoke passes with no " + ValidationLimits + " to gate on:\n" + outcome.Output);
    }
}
