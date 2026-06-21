using System.Threading.Tasks;
using SkathIO.Rogue.Migration.Analyzer;
using Xunit;

namespace SkathIO.Rogue.Migration.Tests;

public sealed class OpenGenericRequestAnalyzerTests
{
    [Fact]
    public async Task ROGM003_Fires_ForOpenGenericRequestType()
    {
        var code = @"
public interface IRequest<T> { }
public class MyRequest<T> : IRequest<T> { }
";
        var diagnostics = await AnalyzerTestHelper.RunAsync(code, new OpenGenericRequestAnalyzer());
        Assert.Contains(diagnostics, d => d.Id == "ROGM003");
    }

    [Fact]
    public async Task ROGM003_DoesNotFire_ForClosedGenericRequestType()
    {
        var code = @"
public interface IRequest<T> { }
public class MyRequest : IRequest<string> { }
";
        var diagnostics = await AnalyzerTestHelper.RunAsync(code, new OpenGenericRequestAnalyzer());
        Assert.DoesNotContain(diagnostics, d => d.Id == "ROGM003");
    }
}
