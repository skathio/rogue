using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// The 2.0.0 docs cleanup. Every package targets net8.0 or later, so the "(net8.0+)" notes in the
/// API reference, the behaviors guide and the getting-started guide are gone. The README loses its
/// "(v1.0)" tags and the "Optional integration packages (v1.0)" heading, states one cold-start
/// figure that matches the committed benchmark run, gives the Jobs-vs-Hangfire and the validation
/// headline results from bench/RESULTS.md, and quotes no measured figure that the write-up of the
/// new run does not hold. The work is committed.
/// </summary>
public sealed class ReadmeCleanupRulesTests
{
    private const string Readme = "README.md";
    private const string Results = "bench/RESULTS.md";

    private const string FigurePattern =
        @"(?<![\d.,])(?<number>\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d+(?:\.\d+)?)\s*(?<unit>ns|µs|μs|us|ms|min|KB|MB|GB|B)\b";

    private const string MultiplierPattern =
        @"(?<![\d.,])(?<number>\d+(?:\.\d+)?)\s*(?:×|x\b|times\b)";

    // ── The (net8.0+) notes ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("docs/api-reference.md")]
    [InlineData("docs/behaviors.md")]
    [InlineData("docs/getting-started.md")]
    public void TheNamedDocs_CarryNoNet8Notes(string doc)
    {
        string[] stale = Lines(doc).Where(static l => l.Contains("(net8.0+)", StringComparison.Ordinal)).ToArray();

        Assert.True(stale.Length == 0, doc + " still carries (net8.0+) notes:\n" + string.Join("\n", stale));
    }

    // ── The (v1.0) tags ───────────────────────────────────────────────────────────────

    [Fact]
    public void TheReadme_HasNoOptionalIntegrationPackagesV10Heading()
    {
        Assert.DoesNotContain("Optional integration packages (v1.0)", RepoFiles.Read(Readme), StringComparison.Ordinal);
    }

    [Fact]
    public void TheReadme_HasNoV10Tags()
    {
        string[] tagged = Lines(Readme).Where(static l => l.Contains("(v1.0)", StringComparison.Ordinal)).ToArray();

        Assert.True(tagged.Length == 0, "README.md still carries (v1.0) tags:\n" + string.Join("\n", tagged));
    }

    // ── The cold-start figure ─────────────────────────────────────────────────────────

    [Fact]
    public void TheReadme_StatesACold_StartFigure()
    {
        Assert.NotEmpty(ColdStartFigures());
    }

    [Fact]
    public void EveryCold_StartFigureInTheReadme_MatchesTheCommittedRun()
    {
        double ratio = CommittedColdStartRatio();
        string[] wrong = ColdStartFigures().Where(f => !MatchesRounded(f, ratio)).ToArray();

        Assert.True(
            wrong.Length == 0,
            "README.md cold-start figures " + string.Join(", ", wrong) + "× do not match the committed ratio of "
            + ratio.ToString(CultureInfo.InvariantCulture) + "×.");
    }

    [Fact]
    public void TheReadme_StatesOneCold_StartFigure()
    {
        string[] distinct = ColdStartFigures()
            .Select(static f => decimal.Parse(f, CultureInfo.InvariantCulture))
            .Distinct()
            .Select(static d => d.ToString(CultureInfo.InvariantCulture))
            .ToArray();

        Assert.True(distinct.Length <= 1, "README.md gives different cold-start figures: " + string.Join(", ", distinct) + ".");
    }

    // ── The headline results ──────────────────────────────────────────────────────────

    [Fact]
    public void TheReadme_StatesAJobsVersusHangfireResult_FromTheWriteUp()
    {
        string section = Section(RepoFiles.Read(Results), "### Jobs", "### Validation");
        string[] lines = Lines(Readme).Where(static l => l.Contains("Hangfire", StringComparison.Ordinal)).ToArray();

        Assert.True(
            lines.Any(l => CarriesFigureFrom(l, section)),
            "No README.md line that names Hangfire carries a figure from the jobs results in bench/RESULTS.md.");
    }

    [Fact]
    public void TheReadme_StatesAValidationResult_FromTheWriteUp()
    {
        string section = Section(RepoFiles.Read(Results), "### Validation", null);
        string[] lines = Lines(Readme).Where(static l => l.Contains("FluentValidation", StringComparison.Ordinal)).ToArray();

        Assert.True(
            lines.Any(l => CarriesFigureFrom(l, section)),
            "No README.md line that names FluentValidation carries a figure from the validation results in bench/RESULTS.md.");
    }

    // ── The README stats ──────────────────────────────────────────────────────────────

    [Fact]
    public void EveryMeasuredFigureInTheReadme_IsInTheWriteUp()
    {
        string results = Normalize(RepoFiles.Read(Results));
        string[] missing = Regex.Matches(Normalize(RepoFiles.Read(Readme)), FigurePattern)
            .Select(static m => m.Groups["number"].Value + " " + m.Groups["unit"].Value)
            .Where(f => !ContainsFigure(results, f))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "README.md quotes figures bench/RESULTS.md does not hold: " + string.Join(", ", missing) + ".");
    }

    // ── Committed ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Readme)]
    [InlineData("docs/api-reference.md")]
    [InlineData("docs/behaviors.md")]
    [InlineData("docs/getting-started.md")]
    public void TheCleanedUpFile_IsCommitted(string path)
    {
        string status = Git("status", "--porcelain", "--", path);

        Assert.True(status.Length == 0, path + " has uncommitted changes:\n" + status);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────

    private static string[] Lines(string relative) => RepoFiles.Read(relative).Split('\n');

    private static string Normalize(string text) => text.Replace("**", string.Empty, StringComparison.Ordinal).Replace('μ', 'µ');

    private static string[] ColdStartFigures() =>
        Lines(Readme)
            .Where(static l => Regex.IsMatch(l, @"(?i)cold[- ]?start"))
            .SelectMany(static l => Regex.Matches(l, MultiplierPattern).Select(static m => m.Groups["number"].Value))
            .ToArray();

    private static bool MatchesRounded(string figure, double ratio)
    {
        int decimals = figure.Contains('.', StringComparison.Ordinal) ? figure.Length - figure.IndexOf('.', StringComparison.Ordinal) - 1 : 0;
        decimal stated = decimal.Parse(figure, CultureInfo.InvariantCulture);
        return Math.Round((decimal)ratio, decimals, MidpointRounding.AwayFromZero) == stated;
    }

    // The newest committed run that holds the cold-start benchmark, read from its CSV report: the
    // Ratio column of the MediatR row is MediatR's mean over Rogue's.
    private static double CommittedColdStartRatio()
    {
        string? csv = Git("ls-files", "--", "bench/results")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(static p => p.EndsWith("ColdStartBenchmarks-report.csv", StringComparison.Ordinal))
            .OrderByDescending(static p => p, StringComparer.Ordinal)
            .FirstOrDefault();
        Assert.NotNull(csv);

        string[] rows = RepoFiles.Read(csv).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        int ratioColumn = Array.IndexOf(rows[0].Trim().Split(','), "Ratio");
        string? mediatR = rows.Skip(1).FirstOrDefault(static r => r.StartsWith("MediatR", StringComparison.Ordinal));
        Assert.True(ratioColumn >= 0, csv + " has no Ratio column.");
        Assert.NotNull(mediatR);

        return double.Parse(mediatR.Trim().Split(',')[ratioColumn], CultureInfo.InvariantCulture);
    }

    private static string Section(string text, string startHeading, string? endHeading)
    {
        int start = text.IndexOf(startHeading, StringComparison.Ordinal);
        Assert.True(start >= 0, Results + " has no section starting with \"" + startHeading + "\".");
        int end = endHeading is null ? -1 : text.IndexOf(endHeading, start + startHeading.Length, StringComparison.Ordinal);
        return Normalize(end < 0 ? text.Substring(start) : text.Substring(start, end - start));
    }

    private static bool CarriesFigureFrom(string line, string section)
    {
        string normalized = Normalize(line);
        bool measured = Regex.Matches(normalized, FigurePattern)
            .Any(m => ContainsFigure(section, m.Groups["number"].Value + " " + m.Groups["unit"].Value));
        bool multiplier = Regex.Matches(normalized, MultiplierPattern)
            .Any(m => ContainsMultiplier(section, m.Groups["number"].Value));
        return measured || multiplier;
    }

    private static bool ContainsFigure(string text, string figure)
    {
        string[] parts = figure.Split(' ');
        return Regex.IsMatch(text, @"(?<![\d.,])" + Regex.Escape(parts[0]) + @"\s*" + Regex.Escape(parts[1]) + @"\b");
    }

    // RESULTS.md words its ratios as "about 100 times" or "9 to 10 times"; the README may write "100×".
    private static bool ContainsMultiplier(string text, string number)
    {
        string n = Regex.Escape(number);
        return Regex.IsMatch(text, @"(?<![\d.,])(" + n + @"(\s+to\s+\d+(\.\d+)?)?|\d+(\.\d+)?\s+to\s+" + n + @")\s*(times\b|×)");
    }

    private static string Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = RepoFiles.Root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using Process git = Process.Start(start)!;
        string output = git.StandardOutput.ReadToEnd();
        string error = git.StandardError.ReadToEnd();
        git.WaitForExit();
        Assert.True(git.ExitCode == 0, "git " + string.Join(" ", arguments) + " failed: " + error);
        return output;
    }
}
