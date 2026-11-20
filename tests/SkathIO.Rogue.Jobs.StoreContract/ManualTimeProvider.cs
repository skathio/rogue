using System;
using System.Collections.Generic;
using System.Threading;

namespace SkathIO.Rogue.Jobs.StoreContract;

/// <summary>
/// A clock that moves only when a test calls <see cref="Advance"/>. Timers created from it (by
/// <c>Task.Delay</c> or a timed <c>CancellationTokenSource</c>) fire during the advance that reaches
/// their due time, so tests of delays and timeouts never wait on real time.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new object();
    private readonly List<ManualTimer> _timers = new List<ManualTimer>();
    private DateTimeOffset _now;

    /// <summary>Starts the clock at <paramref name="start"/>.</summary>
    public ManualTimeProvider(DateTimeOffset start)
    {
        _now = start;
    }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <inheritdoc />
    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    /// <summary>Moves the clock forward and fires every timer that comes due, in due order.</summary>
    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        DateTimeOffset target = GetUtcNow() + by;
        for (ManualTimer? due = NextDue(target); due is not null; due = NextDue(target))
        {
            due.Fire();
        }
    }

    // Moves the clock to the earliest timer due by the target and returns it, rescheduled; or moves the
    // clock to the target and returns null when no timer is due.
    private ManualTimer? NextDue(DateTimeOffset target)
    {
        lock (_gate)
        {
            ManualTimer? next = null;
            foreach (ManualTimer timer in _timers)
            {
                if (timer.DueAt <= target && (next is null || timer.DueAt < next.DueAt))
                {
                    next = timer;
                }
            }
            if (next is null)
            {
                _now = target;
                return null;
            }
            _now = next.DueAt!.Value;
            next.DueAt = next.Period > TimeSpan.Zero ? _now + next.Period : null;
            return next;
        }
    }

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_gate)
        {
            _timers.Add(timer);
        }
        return timer;
    }

    private void Remove(ManualTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public DateTimeOffset? DueAt { get; set; }

        public TimeSpan Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_owner._gate)
            {
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : _owner._now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
            }
            return true;
        }

        public void Fire() => _callback(_state);

        public void Dispose() => _owner.Remove(this);

        public System.Threading.Tasks.ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }
    }
}
