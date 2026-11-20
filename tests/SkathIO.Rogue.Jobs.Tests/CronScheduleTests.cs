using System;
using System.Globalization;
using Xunit;

namespace SkathIO.Rogue.Jobs.Tests;

/// <summary>The in-house five-field cron parser and the occurrences it gives, in UTC.</summary>
public sealed class CronScheduleTests
{
    [Theory]
    [InlineData("30 * * * *", "2026-01-01T00:00", "2026-01-01T00:30")]
    [InlineData("30 * * * *", "2026-01-01T00:30", "2026-01-01T01:30")]
    [InlineData("30 * * * *", "2026-01-01T00:29:59", "2026-01-01T00:30")]
    [InlineData("* * * * *", "2026-01-01T00:00:30", "2026-01-01T00:01")]
    [InlineData("*/15 * * * *", "2026-01-01T00:16", "2026-01-01T00:30")]
    [InlineData("0 9-17/4 * * *", "2026-01-01T10:00", "2026-01-01T13:00")]
    [InlineData("0 0 1 * *", "2026-01-15T00:00", "2026-02-01T00:00")]
    [InlineData("0 0 31 * *", "2026-02-01T00:00", "2026-03-31T00:00")]
    [InlineData("0 0 29 2 *", "2026-01-01T00:00", "2028-02-29T00:00")]
    [InlineData("0 0 * * 0", "2026-01-01T00:00", "2026-01-04T00:00")]
    [InlineData("0 0 * * 7", "2026-01-01T00:00", "2026-01-04T00:00")]
    [InlineData("0 0 * * MON-FRI", "2026-01-02T12:00", "2026-01-05T00:00")]
    [InlineData("0 0 1 JAN,jul *", "2026-02-01T00:00", "2026-07-01T00:00")]
    [InlineData("0 0 59 * *", "2026-01-01T00:00", null)]
    [InlineData("5,10 0 * * *", "2026-01-01T00:05", "2026-01-01T00:10")]
    [InlineData("59 23 31 12 *", "2026-12-31T23:59", "2027-12-31T23:59")]
    public void NextAfter_GivesTheFirstOccurrenceStrictlyAfter(string cron, string after, string? expected)
    {
        if (expected is null)
        {
            Assert.Throws<FormatException>(() => CronSchedule.Parse(cron));
            return;
        }

        DateTimeOffset? next = CronSchedule.Parse(cron).NextAfter(At(after));

        Assert.Equal(At(expected), next);
    }

    [Fact]
    public void BothDayFieldsRestricted_MatchEitherDay()
    {
        // The 13th, or any Friday: 2026-01-02 is a Friday, before the 13th.
        CronSchedule schedule = CronSchedule.Parse("0 0 13 * 5");

        Assert.Equal(At("2026-01-02T00:00"), schedule.NextAfter(At("2026-01-01T00:00")));
        Assert.Equal(At("2026-01-13T00:00"), schedule.NextAfter(At("2026-01-09T00:00")));
    }

    [Fact]
    public void OneDayFieldStarred_MatchesOnlyTheOther()
    {
        CronSchedule schedule = CronSchedule.Parse("0 0 13 * *");

        Assert.Equal(At("2026-01-13T00:00"), schedule.NextAfter(At("2026-01-01T00:00")));
    }

    [Fact]
    public void Occurrences_AreInUtc_WhateverTheOffsetGiven()
    {
        var after = new DateTimeOffset(2026, 1, 1, 2, 0, 0, TimeSpan.FromHours(2));

        Assert.Equal(At("2026-01-01T00:30"), CronSchedule.Parse("30 * * * *").NextAfter(after));
        Assert.Equal(TimeSpan.Zero, CronSchedule.Parse("30 * * * *").NextAfter(after)!.Value.Offset);
    }

    [Theory]
    [InlineData("59 23 31 12 6")]
    [InlineData("0-59 0-23 1-31 1-12 0-7")]
    [InlineData("*/59 */23 */31 */12 */7")]
    [InlineData("  30   *  * * *  ")]
    public void Parse_AcceptsEveryFieldAtItsLimits(string cron)
    {
        Assert.NotNull(CronSchedule.Parse(cron).NextAfter(At("2026-01-01T00:00")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("* * * *")]
    [InlineData("* * * * * *")]
    [InlineData("60 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("* * 0 * *")]
    [InlineData("* * 32 * *")]
    [InlineData("* * * 0 *")]
    [InlineData("* * * 13 *")]
    [InlineData("* * * * 8")]
    [InlineData("*/0 * * * *")]
    [InlineData("*/60 * * * *")]
    [InlineData("5-1 * * * *")]
    [InlineData("a * * * *")]
    [InlineData("-1 * * * *")]
    [InlineData("1,,2 * * * *")]
    [InlineData("100 * * * *")]
    [InlineData("* * * FOO *")]
    [InlineData("0 0 30 2 *")]
    public void Parse_RejectsAnInvalidOrNeverOccurringExpression(string cron)
    {
        Assert.Throws<FormatException>(() => CronSchedule.Parse(cron));
    }

    [Fact]
    public void Parse_RejectsNull_AndAnExpressionPastTheLengthLimit()
    {
        Assert.Throws<ArgumentNullException>(() => CronSchedule.Parse(null!));
        string atLimit = "0" + new string(' ', CronSchedule.MaxLength - 8) + "* * * *";
        Assert.Equal(CronSchedule.MaxLength, atLimit.Length);

        Assert.NotNull(CronSchedule.Parse(atLimit));
        Assert.Throws<FormatException>(() => CronSchedule.Parse(atLimit + " "));
    }

    [Fact]
    public void RecurringJob_NamesItsCommand_WhenTheCronIsInvalid_AndRejectsNull()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => new RecurringJob<RecordValue>("61 * * * *", static () => new RecordValue(1)));

        Assert.Contains(typeof(RecordValue).ToString(), error.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => new RecurringJob<RecordValue>(null!, static () => new RecordValue(1)));
        Assert.Equal("30 * * * *", new RecurringJob<RecordValue>("30 * * * *", null).Cron);
    }

    private static DateTimeOffset At(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
