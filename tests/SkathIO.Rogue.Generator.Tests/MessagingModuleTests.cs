using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// The messaging feature module's output: one binding per <c>[Message]</c> type, dispatched by the
/// type's shape, and nothing at all, with the dispatcher's <c>Publish</c> unchanged, unless the
/// messaging package is referenced.
/// </summary>
public sealed class MessagingModuleTests
{
    private const string HintName = "RogueMessaging.g.cs";

    private const string Messages = @"
using SkathIO.Rogue;
using SkathIO.Rogue.Messaging;
namespace App
{
    [Message(""app.deleted"")] public sealed record Deleted(int Id) : ICommand;
    public sealed class DeletedHandler : ICommandHandler<Deleted>
    {
        public System.Threading.Tasks.ValueTask Handle(Deleted c, System.Threading.CancellationToken ct) => default;
    }
    [Message(""app.reserve"")] public sealed record Reserve(int Id) : ICommand<int>;
    public sealed class ReserveHandler : ICommandHandler<Reserve, int>
    {
        public System.Threading.Tasks.ValueTask<int> Handle(Reserve c, System.Threading.CancellationToken ct) => new System.Threading.Tasks.ValueTask<int>(c.Id);
    }
    [Message(""app.report"")] public sealed record Report(int Id);
    [Message(""app.created"")] public sealed record Created(int Id) : IEvent;
    public sealed class Outer
    {
        [Message(""app.hidden"")] private sealed record Hidden(int Id) : IEvent;
    }
}";

    [Fact]
    public void VoidCommand_IsSentAsAVoidCommand()
    {
        string line = BindingLine("app.deleted");

        Assert.Contains("GetRequiredService<global::SkathIO.Rogue.ISender>(services).Send((global::SkathIO.Rogue.ICommand)message, cancellationToken)", line, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandWithAResponse_IsSentTyped_AndItsResultDropped()
    {
        string line = BindingLine("app.reserve");

        Assert.Contains("MessageBinding.IgnoreResult(", line, StringComparison.Ordinal);
        Assert.Contains(".Send<int>((global::SkathIO.Rogue.ICommand<int>)message, cancellationToken)", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Event_IsPublishedThroughThePublisher()
    {
        Assert.Contains("GetRequiredService<global::SkathIO.Rogue.IPublisher>(services).Publish((global::SkathIO.Rogue.IEvent)message, cancellationToken)", BindingLine("app.created"), StringComparison.Ordinal);
    }

    [Fact]
    public void TypeThatIsNeitherEventNorCommand_GetsABindingWithNoDispatcher()
    {
        Assert.EndsWith("null));", BindingLine("app.report").TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void TypeGeneratedCodeCannotName_GetsNoBinding()
    {
        Assert.DoesNotContain("app.hidden", Generated(HintName), StringComparison.Ordinal);
    }

    [Fact]
    public void WithThePackage_PublishHandsEventsToTheBusAfterLocalHandlers()
    {
        Assert.Contains("MessageForwarding.AfterLocalHandlers(_serviceProvider, ev, PublishLocal(ev, cancellationToken), cancellationToken)", Generated("RogueDispatcher.g.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutThePackage_NothingIsEmitted_AndPublishIsUnchanged()
    {
        const string marker = @"
namespace SkathIO.Rogue.Messaging
{
    [global::System.AttributeUsage(global::System.AttributeTargets.Class)]
    public sealed class MessageAttribute : global::System.Attribute { public MessageAttribute(string name) { } }
}";
        GeneratorDriverRunResult run = GeneratorTestHelper.RunGenerator(marker + Messages);
        string[] hints = run.Results.SelectMany(static r => r.GeneratedSources).Select(static s => s.HintName).ToArray();
        string dispatcher = run.Results.SelectMany(static r => r.GeneratedSources).Single(static s => s.HintName == "RogueDispatcher.g.cs").SourceText.ToString();

        Assert.DoesNotContain(HintName, hints);
        Assert.DoesNotContain("MessageForwarding", dispatcher, StringComparison.Ordinal);
        Assert.DoesNotContain("PublishLocal", dispatcher, StringComparison.Ordinal);
    }

    private static string BindingLine(string name)
    {
        string[] lines = Generated(HintName).Split('\n');
        int at = Array.FindIndex(lines, l => l.Contains("\"" + name + "\"", StringComparison.Ordinal));
        Assert.True(at >= 0, name + " has no binding.");
        return lines[at + 1];
    }

    private static string Generated(string hintName)
    {
        GeneratorDriverRunResult run = GeneratorTestHelper.RunGenerator(Messages, MetadataReference.CreateFromFile(MessagingConsumer.Package().Location));
        Assert.Empty(run.Results.Where(static r => r.Exception is not null));
        return run.Results.SelectMany(static r => r.GeneratedSources).Single(s => s.HintName == hintName).SourceText.ToString();
    }
}
