using System;
using System.IO;
using System.Linq;
using Xunit;

namespace SkathIO.Rogue.Messaging.RabbitMQ.Tests;

/// <summary>
/// What the docs promise about a message that reaches the wait queue with no expiration. The queue's
/// own message TTL runs from the moment a message arrives, and nothing ahead of it stays longer, so it
/// leaves at most 24 hours after it arrived: the docs must give that bound, not a looser one counted
/// from when it reaches the head of the queue.
/// </summary>
public sealed class WaitQueueStrayMessageDocsTests
{
    [Fact]
    public void TheDocs_CountAStrayMessagesTwentyFourHours_FromItsArrival()
    {
        string paragraph = Assert.Single(DocParagraphs(), static p => p.Contains("no expiration", StringComparison.Ordinal));

        Assert.Contains("at most 24 hours after it arrived", paragraph, StringComparison.Ordinal);
        Assert.DoesNotContain("after it reaches the head", paragraph, StringComparison.Ordinal);
    }

    private static string[] DocParagraphs()
    {
        string text = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "messaging-rabbitmq.md")).Replace("\r\n", "\n", StringComparison.Ordinal);
        return text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(static p => p.Replace('\n', ' ')).ToArray();
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "SkathIO.Rogue.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new InvalidOperationException("SkathIO.Rogue.sln not found above " + AppContext.BaseDirectory);
    }
}
