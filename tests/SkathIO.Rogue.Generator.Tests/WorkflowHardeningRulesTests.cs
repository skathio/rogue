using System;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// Workflow hardening. A runner's apt package index can be stale, so every step that installs apt
/// packages refreshes the index first. The release workflow the maintainer dispatches pins
/// actions/checkout and actions/setup-dotnet by the same commit SHAs ci.yml uses, so a moved tag
/// cannot change what runs with the release's permissions. hashira is pinned the same way: ci.yml,
/// publish.yml and site.yml call and check out hashira at one commit SHA, so the required
/// `ci / passed` check and the release run the same hashira code until a deliberate bump.
/// </summary>
public sealed class WorkflowHardeningRulesTests
{
    private const string CiPath = ".github/workflows/ci.yml";
    private const string PublishPath = ".github/workflows/publish.yml";
    private const string SitePath = ".github/workflows/site.yml";
    private const string ShaPin = @"@(?<sha>[0-9a-f]{40})$";

    private static readonly string[] _pinnedActions = { "actions/checkout", "actions/setup-dotnet" };

    [Fact]
    public void Ci_EveryAptInstallStep_RefreshesThePackageIndexFirst()
    {
        string[] installs = Steps(CiPath).Where(static s => s.Contains("apt-get install", StringComparison.Ordinal)).ToArray();

        Assert.NotEmpty(installs);
        Assert.All(installs, static step =>
        {
            int update = step.IndexOf("apt-get update", StringComparison.Ordinal);
            int install = step.IndexOf("apt-get install", StringComparison.Ordinal);
            Assert.True(update >= 0 && update < install, "This ci.yml step installs apt packages without `apt-get update` before it:\n" + step);
        });
    }

    [Fact]
    public void Publish_PinsCheckoutAndSetupDotnet_ByCommitSha()
    {
        string[] uses = PinnedActionRefs(PublishPath);

        Assert.Contains(uses, static u => u.StartsWith("actions/checkout@", StringComparison.Ordinal));
        Assert.Contains(uses, static u => u.StartsWith("actions/setup-dotnet@", StringComparison.Ordinal));
        string[] unpinned = uses.Where(static u => !Regex.IsMatch(u, ShaPin)).ToArray();
        Assert.True(unpinned.Length == 0, "publish.yml uses actions not pinned by a 40-character commit SHA: " + string.Join(", ", unpinned));
    }

    [Fact]
    public void Publish_PinsTheSameShas_AsCi()
    {
        string[] ciRefs = PinnedActionRefs(CiPath).Distinct(StringComparer.Ordinal).ToArray();
        string[] publishRefs = PinnedActionRefs(PublishPath).Distinct(StringComparer.Ordinal).ToArray();

        foreach (string action in _pinnedActions)
        {
            string[] ci = ciRefs.Where(u => u.StartsWith(action + "@", StringComparison.Ordinal)).ToArray();
            string[] publish = publishRefs.Where(u => u.StartsWith(action + "@", StringComparison.Ordinal)).ToArray();
            Assert.True(ci.Length == 1, "ci.yml pins " + action + " at more than one ref, or none: " + string.Join(", ", ci));
            Assert.True(
                publish.SequenceEqual(ci, StringComparer.Ordinal),
                "publish.yml pins " + action + " at " + string.Join(", ", publish) + ", not at ci.yml's " + ci[0] + ".");
        }
    }

    [Fact]
    public void Workflows_PinHashiraAtOneCommitSha()
    {
        string[] ciRefs = HashiraUsesRefs(RepoFiles.Read(CiPath));
        string publish = RepoFiles.Read(PublishPath);
        string[] publishRefs = HashiraUsesRefs(publish)
            .Concat(Regex.Matches(publish, @"(?m)^\s*ref:\s*'?(?<ref>[^'\s#]+)").Select(static m => m.Groups["ref"].Value))
            .ToArray();
        string[] siteRefs = HashiraUsesRefs(RepoFiles.Read(SitePath));

        Assert.Single(ciRefs);
        Assert.Equal(3, publishRefs.Length);
        Assert.Equal(2, siteRefs.Length);
        string[] all = ciRefs.Concat(publishRefs).Concat(siteRefs).ToArray();
        Assert.All(all, static r => Assert.Matches("^[0-9a-f]{40}$", r));
        Assert.Single(all.Distinct(StringComparer.Ordinal));
    }

    // The refs of every skathio/hashira workflow or action a file calls with `uses:`.
    private static string[] HashiraUsesRefs(string workflow) =>
        Regex.Matches(workflow, @"(?m)uses:\s*skathio/hashira/[^\s@]+@(?<ref>[^\s#]+)")
            .Select(static m => m.Groups["ref"].Value)
            .ToArray();

    // The steps of a workflow, split at each list item.
    private static string[] Steps(string path) =>
        Regex.Split(RepoFiles.Read(path).Replace("\r\n", "\n", StringComparison.Ordinal), @"(?m)^(?=\s*- )");

    // The `uses:` refs of a workflow that name actions/checkout or actions/setup-dotnet.
    private static string[] PinnedActionRefs(string path) =>
        Regex.Matches(RepoFiles.Read(path), @"(?m)^\s*(-\s+)?uses:\s*['""]?(?<ref>[^\s'""#]+)")
            .Select(static m => m.Groups["ref"].Value)
            .Where(static u => _pinnedActions.Any(a => u.StartsWith(a + "@", StringComparison.Ordinal)))
            .ToArray();
}
