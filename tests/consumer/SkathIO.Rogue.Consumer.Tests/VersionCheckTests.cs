using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SkathIO.Rogue.Consumer.Tests;

/// <summary>
/// ROGUE013, the core/feature version check, as an app meets it: a project restores the core package
/// at one version and the validation package at another, and the core generator from the package
/// must warn. The same project with matching versions must not. run.sh packs the core package at
/// another minor version into the mismatch feed for this.
/// </summary>
public sealed class VersionCheckTests
{
    private const string MismatchedCoreVersion = "2.1.0-consumer";

    // Guards against a hung build only; the assertions do not depend on time.
    private static readonly TimeSpan _hangGuard = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task ACorePackage_AtAnotherMinorVersion_ThanTheValidationPackage_WarnsRogue013()
    {
        string version = ConsumerPaths.RestoredVersion();

        BuildResult result = await BuildAppAsync("rogue013-mismatch", MismatchedCoreVersion, version);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("warning ROGUE013", result.Output, StringComparison.Ordinal);
        // The generator reports the file versions, which MinVer sets to major.minor.patch.0.
        Assert.Contains("'SkathIO.Rogue.Validation' is version " + version.Split('-')[0] + ".0", result.Output, StringComparison.Ordinal);
        Assert.Contains("core SkathIO.Rogue package is version " + MismatchedCoreVersion.Split('-')[0] + ".0", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSameApp_WithMatchingVersions_HasNoRogue013()
    {
        string version = ConsumerPaths.RestoredVersion();

        BuildResult result = await BuildAppAsync("rogue013-match", version, version);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.DoesNotContain("ROGUE013", result.Output, StringComparison.Ordinal);
    }

    // Writes a one-file app that references the two packages at the given versions, and builds it.
    private static async Task<BuildResult> BuildAppAsync(string name, string coreVersion, string validationVersion)
    {
        Assert.True(Directory.Exists(ConsumerPaths.MismatchFeed), "No mismatch feed at " + ConsumerPaths.MismatchFeed + "; run tests/consumer/run.sh.");
        string dir = Path.Combine(ConsumerPaths.Artifacts, name);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
        Directory.CreateDirectory(dir);

        // These files stop MSBuild and NuGet from reading the repository's own above artifacts/.
        File.WriteAllText(Path.Combine(dir, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(dir, "Directory.Build.targets"), "<Project />");
        File.WriteAllText(Path.Combine(dir, "Directory.Packages.props"), $"""
            <Project>
              <PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="SkathIO.Rogue" Version="{coreVersion}" />
                <PackageVersion Include="SkathIO.Rogue.Validation" Version="{validationVersion}" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "NuGet.config"), """
            <configuration>
              <config><add key="globalPackagesFolder" value="../packages" /></config>
              <packageSources>
                <clear />
                <add key="rogue-local" value="../feed" />
                <add key="rogue-mismatch" value="../feed-mismatch" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="rogue-local"><package pattern="SkathIO.Rogue*" /></packageSource>
                <packageSource key="rogue-mismatch"><package pattern="SkathIO.Rogue*" /></packageSource>
                <packageSource key="nuget.org"><package pattern="*" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        File.WriteAllText(Path.Combine(dir, name + ".csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>disable</ImplicitUsings>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="SkathIO.Rogue" />
                <PackageReference Include="SkathIO.Rogue.Validation" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "Ping.cs"), """
            namespace VersionCheckApp;

            public sealed record Ping : SkathIO.Rogue.IQuery<int>;

            public sealed class PingHandler : SkathIO.Rogue.IQueryHandler<Ping, int>
            {
                public System.Threading.Tasks.ValueTask<int> Handle(Ping query, System.Threading.CancellationToken cancellationToken) => new(1);
            }
            """);

        return await RunDotnetAsync(dir, "build", "--disable-build-servers", "-nologo", "-c", "Release").ConfigureAwait(false);
    }

    private static async Task<BuildResult> RunDotnetAsync(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        // The test host may run under an MSBuild that set these; the child build finds its own.
        foreach (string inherited in new[] { "MSBuildSDKsPath", "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildLoadMicrosoftTargetsReadOnly" })
        {
            start.Environment.Remove(inherited);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start.");
        using var guard = new CancellationTokenSource(_hangGuard);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(guard.Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(guard.Token);
        try
        {
            await process.WaitForExitAsync(guard.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        return new BuildResult(process.ExitCode, await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false));
    }

    private sealed record BuildResult(int ExitCode, string Output);
}
