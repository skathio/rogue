using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace SkathIO.Rogue.Consumer.Tests;

/// <summary>Where run.sh puts the packed packages, and the version this suite restored.</summary>
internal static class ConsumerPaths
{
    private static readonly Lazy<string> _artifacts = new Lazy<string>(FindArtifacts);

    /// <summary>artifacts/consumer at the repository root.</summary>
    public static string Artifacts => _artifacts.Value;

    /// <summary>The twelve packages the suite restores.</summary>
    public static string Feed => Path.Combine(Artifacts, "feed");

    /// <summary>The core package at another minor version, for the ROGUE013 check.</summary>
    public static string MismatchFeed => Path.Combine(Artifacts, "feed-mismatch");

    /// <summary>The package version restored here, read from the core assembly's product version.</summary>
    public static string RestoredVersion()
    {
        string? product = FileVersionInfo.GetVersionInfo(typeof(ISender).Assembly.Location).ProductVersion;
        Assert.False(string.IsNullOrEmpty(product), "The restored SkathIO.Rogue.Abstractions has no product version.");
        int metadata = product.IndexOf('+', StringComparison.Ordinal);
        return metadata < 0 ? product : product.Substring(0, metadata);
    }

    private static string FindArtifacts()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "SkathIO.Rogue.Consumer.sln")))
        {
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        Assert.NotNull(dir);
        string artifacts = Path.GetFullPath(Path.Combine(dir, "..", "..", "artifacts", "consumer"));
        Assert.True(Directory.Exists(artifacts), "No packed packages at " + artifacts + "; run tests/consumer/run.sh.");
        return artifacts;
    }
}
