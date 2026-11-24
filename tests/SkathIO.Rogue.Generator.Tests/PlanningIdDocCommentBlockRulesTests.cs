using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// The generated API reference reads the <c>///</c> comments of the projects that ship a public API,
/// so no doc comment may carry the id of a local planning note: each one is reworded to give its
/// reason in words. These checks read a doc comment as a whole block, so an id broken over two lines
/// or written in a <c>/** */</c> comment is still found, and they run the acceptance check's own
/// pattern. Plain <c>//</c> code comments are out of scope.
/// </summary>
public sealed class PlanningIdDocCommentBlockRulesTests
{
    // The id prefixes are assembled from parts so this file does not itself carry a planning id.
    private const string PlanPrefix = "PD";
    private const string RequirementPrefix = "NFR";

    // The acceptance check's pattern, run on every line that holds a doc comment marker.
    private static readonly Regex _acceptancePattern = new Regex(
        PlanPrefix + "-[0-9]|" + RequirementPrefix + "-[A-Z]+-[0-9]",
        RegexOptions.CultureInvariant);

    // The same ids with whitespace allowed around the hyphen, so an id broken over lines is found.
    private static readonly Regex _idAcrossLines = new Regex(
        @"\b" + PlanPrefix + @"\s*-\s*\d|\b" + RequirementPrefix + @"\s*-\s*[A-Za-z]",
        RegexOptions.CultureInvariant);

    // A paired doc element with nothing left inside it, as when a remark that was only an id is cut.
    private static readonly Regex _emptyElement = new Regex(
        @"<(summary|remarks|returns|value|para)>\s*</\1>",
        RegexOptions.CultureInvariant);

    private static readonly Regex _blockDocComment = new Regex(
        @"/\*\*.*?\*/",
        RegexOptions.CultureInvariant | RegexOptions.Singleline);

    public static TheoryData<string> ApiProjects()
    {
        var data = new TheoryData<string>();
        foreach (string file in Directory.GetFiles(Path.Combine(RepoRoot(), "src"), "PublicAPI.Shipped.txt", SearchOption.AllDirectories))
        {
            data.Add(Path.GetFileName(Path.GetDirectoryName(file)!));
        }
        return data;
    }

    [Fact]
    public void TheAcceptancePattern_MatchesNoDocCommentLine()
    {
        List<string> hits = SourceFiles()
            .SelectMany(file => File.ReadAllLines(file).Select((text, i) => (file, text, line: i + 1)))
            .Where(l => l.text.Contains("///", StringComparison.Ordinal) && _acceptancePattern.IsMatch(l.text))
            .Select(l => Relative(l.file) + ":" + l.line + ": " + l.text.Trim())
            .ToList();

        Assert.True(hits.Count == 0, "Doc comment lines with a planning id:\n" + string.Join("\n", hits));
    }

    [Theory]
    [MemberData(nameof(ApiProjects))]
    public void EachApiProject_HasDocComments_AndNoBlockCarriesAPlanningId(string project)
    {
        List<(string Location, string Text)> blocks = DocCommentBlocks(Path.Combine(RepoRoot(), "src", project));

        Assert.True(blocks.Count > 0, project + " has no /// comments, so the scan would pass vacuously.");
        AssertNoBlock(blocks, b => _idAcrossLines.IsMatch(b), "a planning id");
    }

    [Fact]
    public void NoDocCommentBlock_CarriesAPlanningIdBrokenOverLines()
    {
        AssertNoBlock(DocCommentBlocks(Path.Combine(RepoRoot(), "src")), b => _idAcrossLines.IsMatch(b), "a planning id");
    }

    [Fact]
    public void NoDocCommentBlock_KeepsAnElementEmptiedByACutId()
    {
        AssertNoBlock(DocCommentBlocks(Path.Combine(RepoRoot(), "src")), b => _emptyElement.IsMatch(b), "an empty element");
    }

    [Fact]
    public void NoBlockStyleDocComment_CarriesAPlanningId()
    {
        List<string> hits = SourceFiles()
            .SelectMany(file => _blockDocComment.Matches(File.ReadAllText(file))
                .Where(m => _idAcrossLines.IsMatch(m.Value))
                .Select(m => Relative(file) + ": " + m.Value))
            .ToList();

        Assert.True(hits.Count == 0, "/** */ doc comments with a planning id:\n" + string.Join("\n", hits));
    }

    // The block check must find each id form the request names, whole or broken over two lines,
    // and leave plain prose alone.
    [Theory]
    [InlineData("/// clean break (" + PlanPrefix + "-40).", true)]
    [InlineData("/// ordering (" + PlanPrefix + "-4 / " + PlanPrefix + "-13a).", true)]
    [InlineData("/// zero allocation (" + RequirementPrefix + "-PERF-1).", true)]
    [InlineData("/// clean break (" + PlanPrefix + "-\n/// 40).", true)]
    [InlineData("/// zero allocation (" + RequirementPrefix + "-\n/// PERF-1).", true)]
    [InlineData("/// No shared request-handler base,\n/// so the two contracts stay independent.", false)]
    [InlineData("/// <summary>\n/// </summary>", false)]
    public void TheBlockCheck_FindsEachIdForm(string comment, bool expected)
    {
        Assert.Equal(expected, _idAcrossLines.IsMatch(Join(comment.Split('\n'))));
    }

    [Theory]
    [InlineData("/// <remarks>\n/// </remarks>", true)]
    [InlineData("/// <summary></summary>", true)]
    [InlineData("/// <remarks>Kept apart so each stays independent.</remarks>", false)]
    [InlineData("/// first.<para/>second.", false)]
    public void TheEmptyElementCheck_FlagsOnlyEmptiedElements(string comment, bool expected)
    {
        Assert.Equal(expected, _emptyElement.IsMatch(Join(comment.Split('\n'))));
    }

    private static void AssertNoBlock(List<(string Location, string Text)> blocks, Func<string, bool> bad, string what)
    {
        List<string> hits = blocks.Where(b => bad(b.Text)).Select(b => b.Location + ":\n" + b.Text).ToList();

        Assert.True(hits.Count == 0, hits.Count + " doc comment(s) carry " + what + ":\n" + string.Join("\n\n", hits));
    }

    // Each run of consecutive /// lines in a file, joined into one text with the markers removed.
    private static List<(string Location, string Text)> DocCommentBlocks(string folder) =>
        Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Where(IsSource)
            .SelectMany(FileBlocks)
            .ToList();

    private static IEnumerable<(string Location, string Text)> FileBlocks(string file)
    {
        string[] lines = File.ReadAllLines(file);
        int start = -1;
        for (int i = 0; i <= lines.Length; i++)
        {
            bool marker = i < lines.Length && lines[i].TrimStart().StartsWith("///", StringComparison.Ordinal);
            if (marker && start < 0)
            {
                start = i;
            }
            else if (!marker && start >= 0)
            {
                yield return (Relative(file) + ":" + (start + 1), Join(lines[start..i]));
                start = -1;
            }
        }
    }

    private static string Join(IEnumerable<string> lines) =>
        string.Join("\n", lines.Select(l => l.TrimStart().Substring(3)));

    private static IEnumerable<string> SourceFiles() =>
        Directory.GetFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories).Where(IsSource);

    // Build output under bin/ and obj/ is not source.
    private static bool IsSource(string file)
    {
        string path = file.Replace('\\', '/');
        return !path.Contains("/bin/", StringComparison.Ordinal) && !path.Contains("/obj/", StringComparison.Ordinal);
    }

    private static string Relative(string file) => Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/');

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SkathIO.Rogue.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir.FullName;
    }
}
