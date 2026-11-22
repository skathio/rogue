using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// The 2.0.0 site update. site/index.html shows a Jobs-vs-Hangfire result and a validation result
/// taken from bench/RESULTS.md, and every figure it quotes matches the committed benchmark run,
/// the cold-start multiplier included. The page keeps the title the visual-test script checks for,
/// keeps its light and dark themes, and the change is committed.
/// </summary>
public sealed class SiteResultsRulesTests
{
    private const string Site = "site/index.html";
    private const string Results = "bench/RESULTS.md";

    private const string FigurePattern =
        @"(?<![\d.,])(?<number>\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d+(?:\.\d+)?)\s*(?<unit>ns|µs|us|ms|min|s|KB|MB|GB|B)\b";

    private const string MultiplierPattern =
        @"(?<![\d.,])(?<number>\d+(?:\.\d+)?)\s*(?:×|x\b|times\b)";

    // ── The Jobs and validation results ───────────────────────────────────────────────

    [Fact]
    public void TheSite_ShowsAJobsVersusHangfireResult_FromTheWriteUp()
    {
        string section = Section("### Jobs", "### Validation");
        string[] blocks = SiteBlocks().Where(static b => b.Contains("Hangfire", StringComparison.Ordinal)).ToArray();

        Assert.True(
            blocks.Any(b => CarriesFigureFrom(b, section)),
            "No block of site/index.html that names Hangfire carries a figure from the jobs results in bench/RESULTS.md.");
    }

    [Fact]
    public void TheSite_ShowsAValidationResult_FromTheWriteUp()
    {
        string section = Section("### Validation", null);
        string[] blocks = SiteBlocks().Where(static b => b.Contains("alidat", StringComparison.Ordinal)).ToArray();

        Assert.True(
            blocks.Any(b => CarriesFigureFrom(b, section)),
            "No block of site/index.html that names validation carries a figure from the validation results in bench/RESULTS.md.");
    }

    // ── The stats match the committed run ─────────────────────────────────────────────

    [Fact]
    public void EveryMeasuredFigureOnTheSite_IsInTheWriteUp()
    {
        string results = Normalize(RepoFiles.Read(Results));
        string[] missing = SiteBlocks()
            .SelectMany(static b => Regex.Matches(b, FigurePattern))
            .Select(static m => m.Groups["number"].Value + " " + m.Groups["unit"].Value)
            .Where(f => !ContainsFigure(results, f))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(missing.Length == 0, "site/index.html quotes figures bench/RESULTS.md does not hold: " + string.Join(", ", missing) + ".");
    }

    [Fact]
    public void EveryMultiplierOnTheSite_IsInTheWriteUp()
    {
        string results = Normalize(RepoFiles.Read(Results));
        string[] missing = SiteBlocks()
            .SelectMany(static b => Regex.Matches(b, MultiplierPattern))
            .Select(static m => m.Groups["number"].Value)
            .Where(n => !ContainsMultiplier(results, n))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(missing.Length == 0, "site/index.html quotes multipliers bench/RESULTS.md does not hold: " + string.Join("×, ", missing) + "×.");
    }

    [Fact]
    public void EveryCold_StartFigureOnTheSite_MatchesTheCommittedRun()
    {
        double ratio = CommittedColdStartRatio();
        string[] wrong = SiteBlocks()
            .Where(static b => Regex.IsMatch(b, @"(?i)cold[- ]?start"))
            .SelectMany(static b => Regex.Matches(b, MultiplierPattern).Select(static m => m.Groups["number"].Value))
            .Where(f => !MatchesRounded(f, ratio))
            .ToArray();

        Assert.True(
            wrong.Length == 0,
            "site/index.html cold-start figures " + string.Join(", ", wrong) + "× do not match the committed ratio of "
            + ratio.ToString(CultureInfo.InvariantCulture) + "×.");
    }

    // ── The page the visual test expects ──────────────────────────────────────────────

    [Fact]
    public void TheSite_KeepsTheTitleTheScreenshotScriptChecks()
    {
        Match title = Regex.Match(RepoFiles.Read(Site), @"<title>(?<text>[^<]*)</title>", RegexOptions.IgnoreCase);

        Assert.True(title.Success, "site/index.html has no <title>.");
        Assert.Equal("Rogue", title.Groups["text"].Value.Trim());
    }

    [Fact]
    public void TheSite_KeepsItsLightAndDarkThemes()
    {
        string html = RepoFiles.Read(Site);

        Assert.Contains("prefers-color-scheme:dark", Regex.Replace(html, @"\s+", string.Empty), StringComparison.Ordinal);
        Assert.Matches(@"\[data-theme=""?dark""?\]", html);
        Assert.Matches(@"<script[^>]*src=""?theme\.js""?", html);
    }

    // ── Committed ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheSite_IsCommitted()
    {
        string status = Git("status", "--porcelain", "--", Site);

        Assert.True(status.Length == 0, Site + " has uncommitted changes:\n" + status);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────

    // The visible text of the page, one entry per block element (a stat, a card, a paragraph, a
    // table row), so that a figure and the words around it are read together.
    private static string[] SiteBlocks()
    {
        string html = Regex.Replace(RepoFiles.Read(Site), @"<(style|script)\b[^>]*>.*?</\1\s*>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        const string BlockTag = @"<\s*/?\s*(?:div|p|tr|li|ul|ol|h[1-6]|section|header|footer|nav|pre|table|body|main|article|dl|dt|dd|figure|figcaption|blockquote)\b[^>]*>";
        return Regex.Split(html, BlockTag, RegexOptions.IgnoreCase)
            .Select(static c => Normalize(WebUtility.HtmlDecode(Regex.Replace(c, "<[^>]+>", " "))))
            .Where(static c => c.Trim().Length > 0)
            .ToArray();
    }

    private static string Normalize(string text) => text.Replace("**", string.Empty, StringComparison.Ordinal).Replace('μ', 'µ');

    private static string Section(string startHeading, string? endHeading)
    {
        string text = RepoFiles.Read(Results);
        int start = text.IndexOf(startHeading, StringComparison.Ordinal);
        Assert.True(start >= 0, Results + " has no section starting with \"" + startHeading + "\".");
        int end = endHeading is null ? -1 : text.IndexOf(endHeading, start + startHeading.Length, StringComparison.Ordinal);
        return Normalize(end < 0 ? text.Substring(start) : text.Substring(start, end - start));
    }

    private static bool CarriesFigureFrom(string block, string section)
    {
        bool measured = Regex.Matches(block, FigurePattern)
            .Any(m => ContainsFigure(section, m.Groups["number"].Value + " " + m.Groups["unit"].Value));
        bool multiplier = Regex.Matches(block, MultiplierPattern)
            .Any(m => ContainsMultiplier(section, m.Groups["number"].Value));
        return measured || multiplier;
    }

    private static bool ContainsFigure(string text, string figure)
    {
        string[] parts = figure.Split(' ');
        return Regex.IsMatch(text, @"(?<![\d.,])" + Regex.Escape(parts[0]) + @"\s*" + Regex.Escape(parts[1]) + @"\b");
    }

    // RESULTS.md words its ratios as "~20.7×", "about 100 times" or "9 to 10 times"; the site may
    // write "100×" or "9–10×".
    private static bool ContainsMultiplier(string text, string number)
    {
        string n = Regex.Escape(number);
        return Regex.IsMatch(text, @"(?<![\d.,])(" + n + @"(\s+to\s+\d+(\.\d+)?)?|\d+(\.\d+)?\s+to\s+" + n + @")\s*(times\b|×)");
    }

    private static bool MatchesRounded(string figure, double ratio)
    {
        int dot = figure.IndexOf('.', StringComparison.Ordinal);
        int decimals = dot < 0 ? 0 : figure.Length - dot - 1;
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
