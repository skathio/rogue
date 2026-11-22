using System;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// Workflow hardening. A runner's apt package index can be stale, so every step that installs apt
/// packages refreshes the index first. The release workflow the maintainer dispatches pins
/// actions/checkout and actions/setup-dotnet by the same commit SHAs ci.yml uses, so a moved tag
/// cannot change what runs with the release's permissions. hashira's rolling major tag stays as is:
/// it is pinned twice on purpose, and other rules hold the two pins equal.
/// </summary>
public sealed class WorkflowHardeningRulesTests
{
    private const string CiPath = ".github/workflows/ci.yml";
    private const string PublishPath = ".github/workflows/publish.yml";
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
    public void Publish_KeepsHashirasRollingMajorTag()
    {
        string publish = RepoFiles.Read(PublishPath);

        Assert.Matches(@"(?m)uses:\s*skathio/hashira/[^\s@]+@v2\b", publish);
        Assert.Matches(@"(?m)^\s*ref:\s*v2\b", publish);
    }

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
