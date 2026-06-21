using System.Threading.Tasks;
using SkathIO.Rogue.Migration.Analyzer;
using Xunit;

namespace SkathIO.Rogue.Migration.Tests;

public sealed class TaskReturnTypeAnalyzerTests
{
    [Fact]
    public async Task ROGM002_Fires_WhenHandlerReturnsTask()
    {
        var code = @"
using System.Threading;
using System.Threading.Tasks;
public interface IRequestHandler<TReq, TRes> { }
public class MyHandler : IRequestHandler<string, int>
{
    public Task<int> Handle(string request, CancellationToken ct) => Task.FromResult(0);
}
";
        var diagnostics = await AnalyzerTestHelper.RunAsync(code, new TaskReturnTypeAnalyzer());
        Assert.Contains(diagnostics, d => d.Id == "ROGM002");
    }

    [Fact]
    public async Task ROGM002_DoesNotFire_WhenHandlerReturnsValueTask()
    {
        var code = @"
using System.Threading;
using System.Threading.Tasks;
public interface IRequestHandler<TReq, TRes> { }
public class MyHandler : IRequestHandler<string, int>
{
    public ValueTask<int> Handle(string request, CancellationToken ct) => new ValueTask<int>(0);
}
";
        var diagnostics = await AnalyzerTestHelper.RunAsync(code, new TaskReturnTypeAnalyzer());
        Assert.DoesNotContain(diagnostics, d => d.Id == "ROGM002");
    }

    [Fact]
    public async Task ROGM002_CodeFix_ReplacesTask_WithValueTask()
    {
        var code = @"
using System.Threading;
using System.Threading.Tasks;
public interface IRequestHandler<TReq, TRes> { }
public class MyHandler : IRequestHandler<string, int>
{
    public Task<int> Handle(string request, CancellationToken ct) => Task.FromResult(0);
}
";
        var fixedCode = await AnalyzerTestHelper.ApplyCodeFixAsync(
            code, new TaskReturnTypeAnalyzer(), new ReplaceTaskReturnTypeCodeFix());
        Assert.Contains("ValueTask<int>", fixedCode);
        // No bare (un-prefixed) Task<int> return type survives. ValueTask<int> contains the
        // substring "Task<int>", so guard against the corruption bug via the leading space that
        // precedes a bare return-type token but not the 'Value'-prefixed one.
        Assert.DoesNotContain(" Task<int>", fixedCode.Substring(fixedCode.IndexOf("public ")));
    }

    [Fact]
    public async Task ROGM002_CodeFix_HandlesQualifiedTask()
    {
        var code = @"
using System.Threading;
public interface IRequestHandler<TReq, TRes> { }
public class MyHandler : IRequestHandler<string, int>
{
    public System.Threading.Tasks.Task<int> Handle(string request, CancellationToken ct)
        => System.Threading.Tasks.Task.FromResult(0);
}
";
        var fixedCode = await AnalyzerTestHelper.ApplyCodeFixAsync(
            code, new TaskReturnTypeAnalyzer(), new ReplaceTaskReturnTypeCodeFix());
        Assert.Contains("ValueTask<int>", fixedCode);
        // No bare (un-prefixed) Task<int> return type survives. ValueTask<int> contains the
        // substring "Task<int>", so guard against the corruption bug via the leading space that
        // precedes a bare return-type token but not the 'Value'-prefixed one.
        Assert.DoesNotContain(" Task<int>", fixedCode.Substring(fixedCode.IndexOf("public ")));
    }
}
