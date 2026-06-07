using System.Threading.Tasks;
using SkathIO.Rogue.Migration.Analyzer;
using Xunit;

namespace SkathIO.Rogue.Migration.Tests;

public sealed class UsingMediatRAnalyzerTests
{
    [Fact]
    public async Task ROGM001_Fires_WhenUsingMediatRPresent()
    {
        var code = "using MediatR;\npublic class C { }";
        var diagnostics = await AnalyzerTestHelper.RunAsync(code, new UsingMediatRAnalyzer());
        Assert.Contains(diagnostics, d => d.Id == "ROGM001");
    }

    [Fact]
    public async Task ROGM001_DoesNotFire_WhenUsingSkathIORogue()
    {
        var code = "using SkathIO.Rogue;\npublic class C { }";
        var diagnostics = await AnalyzerTestHelper.RunAsync(code, new UsingMediatRAnalyzer());
        Assert.DoesNotContain(diagnostics, d => d.Id == "ROGM001");
    }

    [Fact]
    public async Task ROGM001_DoesNotFire_WhenNoMediatRUsing()
    {
        var code = "public class C { }";
        var diagnostics = await AnalyzerTestHelper.RunAsync(code, new UsingMediatRAnalyzer());
        Assert.DoesNotContain(diagnostics, d => d.Id == "ROGM001");
    }
}
