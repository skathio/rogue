using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SkathIO.Rogue.Messaging.RabbitMQ.Tests;

/// <summary>
/// The delivery stamp key is counted, and used, as its UTF-8 bytes, so it must be text that has UTF-8
/// bytes of its own. A key holding half of a surrogate pair has none: encoding it puts the same three
/// replacement bytes in place of each half, so different keys would sign alike and a key could reach
/// 32 bytes without holding 32 bytes of secret. Such a key is refused like one that is too short.
/// These tests need no broker.
/// </summary>
public sealed class DeliveryStampKeyEncodingTests
{
    private const string LoneHigh = "\uD800";
    private const string LoneLow = "\uDC00";
    private const string Pair = "🔑";
    private const string Valid32 = "0123456789abcdef0123456789abcdef";

    // Named, since two keys of different halves print alike and the runner would take them for one case.
    private static readonly Dictionary<string, string> _keys = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Eleven halves would count as 33 bytes if each were replaced.
        ["eleven high halves"] = Repeat(LoneHigh, 11),
        ["eleven low halves"] = Repeat(LoneLow, 11),
        // Long enough without the half, which still has no bytes of its own.
        ["a high half last"] = Valid32 + LoneHigh,
        ["a low half first"] = LoneLow + Valid32,
        ["a high half inside"] = Valid32.Substring(0, 16) + LoneHigh + Valid32.Substring(16),
        // The halves in the wrong order are two halves, not a pair.
        ["a pair reversed"] = Valid32 + "\uDD11\uD83D",
        // A whole pair is four bytes: eight are 32, seven and three more bytes are 31.
        ["eight pairs"] = Repeat(Pair, 8),
        ["seven pairs and three bytes"] = Repeat(Pair, 7) + "abc",
        ["seven pairs and four bytes"] = Repeat(Pair, 7) + "abcd",
        ["plain text of 32 bytes"] = Valid32,
    };

    /// <summary>A key by its name, and whether registration accepts it.</summary>
    public static TheoryData<string, bool> Keys => new TheoryData<string, bool>
    {
        { "eleven high halves", false },
        { "eleven low halves", false },
        { "a high half last", false },
        { "a low half first", false },
        { "a high half inside", false },
        { "a pair reversed", false },
        { "eight pairs", true },
        { "seven pairs and three bytes", false },
        { "seven pairs and four bytes", true },
        { "plain text of 32 bytes", true },
    };

    [Theory]
    [MemberData(nameof(Keys))]
    public void AKeyThatIsNotWellFormedText_IsRefused_WhenTheTransportIsRegistered(string name, bool accepted)
    {
        string key = _keys[name];

        Exception? thrown = Record.Exception(() => new ServiceCollection().AddRogueRabbitMqTransport(o => o.DeliveryStampKey = key));

        AssertChecked(thrown, accepted);
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void AKeyThatIsNotWellFormedText_IsRefused_WhenTheTransportIsBuilt(string name, bool accepted)
    {
        var options = new RabbitMqTransportOptions { DeliveryStampKey = _keys[name] };

        Exception? thrown = Record.Exception(() => new RabbitMqTransport(options, Array.Empty<string>()));

        AssertChecked(thrown, accepted);
    }

    [Fact]
    public void TheRefusal_NamesTheOption_AndShowsNoPartOfTheKey()
    {
        string key = "s3cret-part-" + Valid32 + LoneHigh;

        Exception? thrown = Record.Exception(() => new ServiceCollection().AddRogueRabbitMqTransport(o => o.DeliveryStampKey = key));

        ArgumentException refused = Assert.IsAssignableFrom<ArgumentException>(thrown);
        string text = refused.ToString();
        Assert.Equal(nameof(RabbitMqTransportOptions.DeliveryStampKey), refused.ParamName);
        Assert.DoesNotContain("s3cret-part", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Valid32, text, StringComparison.Ordinal);
        Assert.DoesNotContain("D800", text, StringComparison.OrdinalIgnoreCase);
        Assert.Null(refused.InnerException);
    }

    private static void AssertChecked(Exception? thrown, bool accepted)
    {
        if (accepted)
        {
            Assert.Null(thrown);
        }
        else
        {
            ArgumentException refused = Assert.IsAssignableFrom<ArgumentException>(thrown);
            Assert.Equal(nameof(RabbitMqTransportOptions.DeliveryStampKey), refused.ParamName);
        }
    }

    private static string Repeat(string text, int times) => string.Concat(Enumerable.Repeat(text, times));
}
