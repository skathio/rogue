using System;

namespace SkathIO.Rogue.Jobs;

/// <summary>
/// A five-field cron expression, parsed in-house, and the occurrences it gives, in UTC to the minute.
/// The fields are minute, hour, day of month, month and day of week; see <see cref="RecurringAttribute"/>
/// for the syntax.
/// </summary>
internal sealed class CronSchedule
{
    /// <summary>The longest expression accepted, in characters; a longer one is not parsed at all.</summary>
    public const int MaxLength = 256;

    // The calendar repeats every 28 years between century years, so a schedule that has no occurrence
    // within that span has none at all.
    private const int SearchYears = 28;

    private static readonly Field _minute = new Field("minute", 0, 59, null, 0);
    private static readonly Field _hour = new Field("hour", 0, 23, null, 0);
    private static readonly Field _dayOfMonth = new Field("day of month", 1, 31, null, 0);
    private static readonly Field _month = new Field("month", 1, 12, new[] { "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC" }, 1);
    private static readonly Field _dayOfWeek = new Field("day of week", 0, 7, new[] { "SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT" }, 0);

    // The allowed values of each field, as bit sets: minute, hour, day of month, month, day of week.
    private readonly ulong[] _sets;

    // A day matches when both day fields match, unless both are restricted: then either one is enough.
    private readonly bool _eitherDay;

    private CronSchedule(string expression, ulong[] sets, bool eitherDay)
    {
        Expression = expression;
        _sets = sets;
        _eitherDay = eitherDay;
    }

    /// <summary>The expression as given.</summary>
    public string Expression { get; }

    /// <summary>Parses <paramref name="expression"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> is null.</exception>
    /// <exception cref="FormatException">The expression is not a valid five-field cron expression, or it never occurs.</exception>
    public static CronSchedule Parse(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (expression.Length > MaxLength)
        {
            throw new FormatException("A cron expression is at most " + MaxLength + " characters long.");
        }
        string[] fields = expression.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            throw new FormatException("A cron expression has five fields (minute, hour, day of month, month, day of week); '" + expression + "' has " + fields.Length + ".");
        }

        // Day of week 7 is Sunday, as 0 is.
        ulong weekdays = _dayOfWeek.Parse(fields[4]);
        if ((weekdays & (1UL << 7)) != 0)
        {
            weekdays = (weekdays & ~(1UL << 7)) | 1UL;
        }
        ulong[] sets = { _minute.Parse(fields[0]), _hour.Parse(fields[1]), _dayOfMonth.Parse(fields[2]), _month.Parse(fields[3]), weekdays };
        var schedule = new CronSchedule(expression, sets, fields[2][0] != '*' && fields[4][0] != '*');

        if (schedule.NextAfter(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero)) is null)
        {
            throw new FormatException("The cron expression '" + expression + "' never occurs.");
        }
        return schedule;
    }

    /// <summary>
    /// The first occurrence strictly after <paramref name="after"/>, in UTC, or <see langword="null"/>
    /// when there is none within the calendar's repeat span.
    /// </summary>
    public DateTimeOffset? NextAfter(DateTimeOffset after)
    {
        DateTime start = after.UtcDateTime;
        if (start.Year >= DateTime.MaxValue.Year - SearchYears)
        {
            return null;
        }
        var t = new DateTime(start.Year, start.Month, start.Day, start.Hour, start.Minute, 0, DateTimeKind.Utc).AddMinutes(1);
        int lastYear = t.Year + SearchYears;
        while (t.Year <= lastYear)
        {
            if (!Has(_sets[3], t.Month))
            {
                t = new DateTime(t.Year, t.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
            }
            else if (!DayMatches(t))
            {
                t = t.Date.AddDays(1);
            }
            else if (!Has(_sets[1], t.Hour))
            {
                t = t.Date.AddHours(t.Hour + 1);
            }
            else if (!Has(_sets[0], t.Minute))
            {
                t = t.AddMinutes(1);
            }
            else
            {
                return new DateTimeOffset(t, TimeSpan.Zero);
            }
        }
        return null;
    }

    private bool DayMatches(DateTime t)
    {
        bool day = Has(_sets[2], t.Day);
        bool weekday = Has(_sets[4], (int)t.DayOfWeek);
        return _eitherDay ? day || weekday : day && weekday;
    }

    private static bool Has(ulong set, int value) => (set & (1UL << value)) != 0;

    /// <summary>One field's name, range and value names (the first name standing for <see cref="FirstName"/>).</summary>
    private sealed record Field(string Name, int Min, int Max, string[]? Names, int FirstName)
    {
        // A comma list of items, each '*', a value, or a range, optionally with a step: '*/n', 'a-b/n', 'a/n'.
        public ulong Parse(string field)
        {
            ulong set = 0;
            foreach (string item in field.Split(','))
            {
                set |= ParseItem(item);
            }
            return set;
        }

        private ulong ParseItem(string item)
        {
            int slash = item.IndexOf('/', StringComparison.Ordinal);
            string range = slash >= 0 ? item.Substring(0, slash) : item;
            int step = slash >= 0 ? Step(item.Substring(slash + 1), item) : 1;
            (int low, int high) = Bounds(range, item, open: slash >= 0);
            if (low < Min || high > Max || low > high)
            {
                throw new FormatException("'" + item + "' is out of range for the " + Name + " field, which takes " + Min + " to " + Max + ".");
            }

            ulong set = 0;
            for (int value = low; value <= high; value += step)
            {
                set |= 1UL << value;
            }
            return set;
        }

        private int Step(string text, string item)
        {
            int step = Number(text, item, named: false);
            if (step < 1 || step > Max)
            {
                throw new FormatException("The step in '" + item + "' (" + Name + ") must be from 1 to " + Max + ".");
            }
            return step;
        }

        // '*' is the whole range; 'a-b' is a range; a lone value is itself, or runs to the maximum when stepped.
        private (int Low, int High) Bounds(string range, string item, bool open)
        {
            if (range == "*")
            {
                return (Min, Max);
            }
            int dash = range.IndexOf('-', StringComparison.Ordinal);
            int low = Number(dash >= 0 ? range.Substring(0, dash) : range, item, named: true);
            if (dash >= 0)
            {
                return (low, Number(range.Substring(dash + 1), item, named: true));
            }
            return (low, open ? Max : low);
        }

        private int Number(string text, string item, bool named)
        {
            int index = named && Names is not null ? Array.FindIndex(Names, n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase)) : -1;
            if (index >= 0)
            {
                return index + FirstName;
            }
            if (text.Length == 0 || text.Length > 2 || !char.IsAsciiDigit(text[0]) || !char.IsAsciiDigit(text[^1]))
            {
                throw new FormatException("'" + item + "' is not a valid " + Name + " value.");
            }
            return int.Parse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
