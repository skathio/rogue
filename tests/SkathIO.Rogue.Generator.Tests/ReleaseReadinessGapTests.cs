using System;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// The release asks for docs/benchmarks.md to follow the committed benchmark run wherever the
/// numbers changed. Its notification fan-out table presents the current Rogue-against-MediatR
/// figures, so each one must be a figure bench/RESULTS.md holds for the newest run.
/// </summary>
public sealed class ReleaseReadinessGapTests
{
    [Fact]
    public void TheFanOutTableInTheBenchmarksPage_QuotesFiguresTheWriteUpHolds()
    {
        string results = RepoFiles.Read("bench/RESULTS.md");
        string[] rows = RepoFiles.Read("docs/benchmarks.md")
            .Split('\n')
            .Where(static line => line.StartsWith("| Publish N=", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(3, rows.Length);

        string[] missing = rows
            .SelectMany(static row => Regex.Matches(row, @"\d[\d,.]*\d ns"))
            .Select(static match => match.Value)
            .Where(figure => !results.Contains(figure, StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "docs/benchmarks.md quotes fan-out figures bench/RESULTS.md does not hold: " + string.Join(", ", missing) + ".");
    }
}
