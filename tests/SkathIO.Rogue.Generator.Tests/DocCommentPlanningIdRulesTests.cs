using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// The public API reference is generated from the <c>///</c> comments in <c>src/</c>, so those
/// comments must not carry the ids of local planning notes, which are not in the public repo. Each
/// comment that cited one is reworded to state its reason in words: no id is left behind, no
/// pointer into the planning notes, and no empty parenthesis where an id was cut out. Plain
/// <c>//</c> code comments are out of scope.
/// </summary>
public sealed class DocCommentPlanningIdRulesTests
{
    // The id prefixes are assembled from parts so this file does not itself carry a planning id.
    private const string PlanPrefix = "PD";
    private const string RequirementPrefix = "NFR";

    // A planning decision id: the prefix, a hyphen, then a number with an optional letter suffix.
    private static readonly Regex _planningDecisionId =
        new Regex(@"\b" + PlanPrefix + @"-\d", RegexOptions.CultureInvariant);

    // A non-functional requirement id: the prefix, a hyphen, then its area and number.
    private static readonly Regex _requirementId =
        new Regex(@"\b" + RequirementPrefix + @"-[A-Za-z0-9]", RegexOptions.CultureInvariant);

    // A pointer into the gitignored planning notes: the notes folder or its decisions file.
    private static readonly Regex _planningNotePointer =
        new Regex(@"\.somi\b|\bdecisions\.md\b", RegexOptions.CultureInvariant);

    // What is left when an id is cut out of a parenthesis instead of reworded: "( )", "(, x)",
    // "(x / )". A call such as "Send()" is not matched, because a name precedes its parenthesis,
    // and code spans and tags are removed before this check runs.
    private static readonly Regex _leftoverParenthesis = new Regex(
        @"(?<![\w>\]\)])\(\s*[,;/]?\s*\)|\(\s*[,;/]\s|\s[,;/]\s*\)",
        RegexOptions.CultureInvariant);

    [Fact]
    public void TheScan_FindsDocCommentLines()
    {
        Assert.True(DocCommentLines().Count > 0, "No /// lines were found under src/, so the other checks would pass vacuously.");
    }

    [Fact]
    public void NoDocCommentLine_CarriesAPlanningDecisionId()
    {
        AssertNone(_planningDecisionId, "a planning decision id");
    }

    [Fact]
    public void NoDocCommentLine_CarriesARequirementId()
    {
        AssertNone(_requirementId, "a non-functional requirement id");
    }

    [Fact]
    public void NoDocCommentLine_PointsIntoThePlanningNotes()
    {
        AssertNone(_planningNotePointer, "a pointer into the planning notes");
    }

    [Fact]
    public void NoDocCommentLine_KeepsAnEmptyParenthesisWhereAnIdWasCut()
    {
        List<string> hits = DocCommentLines()
            .Where(line => _leftoverParenthesis.IsMatch(Prose(line.Text)))
            .Select(line => line.Location + ": " + line.Text.Trim())
            .ToList();

        Assert.True(
            hits.Count == 0,
            hits.Count + " /// line(s) under src/ keep a parenthesis left empty or dangling where an id was removed:\n"
            + string.Join("\n", hits));
    }

    // The scan must recognise each id form the request names: one id, two ids side by side, an id
    // with a letter suffix, and a requirement id with its area.
    [Theory]
    [InlineData(PlanPrefix + "-40", true)]
    [InlineData("(" + PlanPrefix + "-4 / " + PlanPrefix + "-13a)", true)]
    [InlineData(PlanPrefix + "-13a ordering", true)]
    [InlineData("zero allocation (" + RequirementPrefix + "-PERF-1)", true)]
    [InlineData("never logged (" + RequirementPrefix + "-SEC-2).", true)]
    [InlineData("No shared request-handler base, so the two contracts stay independent.", false)]
    public void TheScan_RecognisesThePlanningIdForms(string line, bool expected)
    {
        bool found = _planningDecisionId.IsMatch(line) || _requirementId.IsMatch(line);

        Assert.Equal(expected, found);
    }

    [Theory]
    [InlineData("calls <c>next()</c>, so the handler never runs.", false)]
    [InlineData("<c>IServiceScopeFactory.CreateScope()</c>", false)]
    [InlineData("via <c>GetRequiredService&lt;THandler&gt;()</c> on every call", false)]
    [InlineData("<c>next = () =&gt; handler.Handle(request, ct)</c> with the", false)]
    [InlineData("no shared base ().", true)]
    [InlineData("the ordering sort key ( / ).", true)]
    [InlineData("deduplication (, a behavior present twice)", true)]
    public void TheLeftoverCheck_FlagsOnlyCutIds(string line, bool expected)
    {
        Assert.Equal(expected, _leftoverParenthesis.IsMatch(Prose(line)));
    }

    private static void AssertNone(Regex pattern, string what)
    {
        List<string> hits = DocCommentLines()
            .Where(line => pattern.IsMatch(line.Text))
            .Select(line => line.Location + ": " + line.Text.Trim())
            .ToList();

        Assert.True(
            hits.Count == 0,
            hits.Count + " /// line(s) under src/ carry " + what + ":\n" + string.Join("\n", hits));
    }

    // The prose of a doc comment line, with each inline code span and XML tag replaced by a word.
    private static string Prose(string line) =>
        _tag.Replace(_codeSpan.Replace(line, "code"), "ref");

    private static readonly Regex _codeSpan = new Regex(@"<c>.*?</c>", RegexOptions.CultureInvariant);

    private static readonly Regex _tag = new Regex(@"<[^>]*>", RegexOptions.CultureInvariant);

    private static List<(string Location, string Text)> DocCommentLines()
    {
        string root = RepoRoot();
        var lines = new List<(string Location, string Text)>();
        foreach (string file in Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            string[] text = File.ReadAllLines(file);
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i].Contains("///", StringComparison.Ordinal))
                {
                    lines.Add((relative + ":" + (i + 1), text[i]));
                }
            }
        }
        return lines;
    }

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
