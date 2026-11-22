using System;
using System.Collections.Generic;

namespace SkathIO.Rogue.Jobs;

// The in-memory store's records: a job, a queue's ready order and its processing set.
public sealed partial class InMemoryJobStore
{
    private sealed class QueueIndex
    {
        public SortedSet<ReadyKey> Ready { get; } = new SortedSet<ReadyKey>();

        public HashSet<Guid> Processing { get; } = new HashSet<Guid>();
    }

    private readonly record struct ReadyKey(DateTimeOffset RunAt, long Sequence, Guid Id) : IComparable<ReadyKey>
    {
        public int CompareTo(ReadyKey other)
        {
            int byTime = RunAt.CompareTo(other.RunAt);
            return byTime != 0 ? byTime : Sequence.CompareTo(other.Sequence);
        }
    }

    private sealed class Entry
    {
        public Entry(NewJob job, DateTimeOffset runAt, long sequence)
        {
            Id = job.Id;
            Kind = job.Kind;
            Queue = job.Queue;
            Payload = job.Payload.ToArray();
            MaxAttempts = job.MaxAttempts;
            UniqueKey = job.UniqueKey;
            RunAt = runAt;
            Sequence = sequence;
        }

        public Guid Id { get; }

        public string Kind { get; }

        public string Queue { get; }

        public ReadOnlyMemory<byte> Payload { get; }

        public int MaxAttempts { get; }

        public string? UniqueKey { get; }

        public JobState State { get; set; }

        public int Attempt { get; set; }

        public DateTimeOffset RunAt { get; set; }

        public long Sequence { get; set; }

        public DateTimeOffset LeaseUntil { get; set; }

        public string? LastError { get; set; }

        public ReadyKey ReadyKey => new ReadyKey(RunAt, Sequence, Id);

        public ClaimedJob ToClaimed() => new ClaimedJob
        {
            Id = Id,
            Kind = Kind,
            Queue = Queue,
            Payload = Payload,
            Attempt = Attempt,
            MaxAttempts = MaxAttempts,
        };

        public JobSnapshot ToSnapshot() => new JobSnapshot
        {
            Id = Id,
            Kind = Kind,
            Queue = Queue,
            State = State,
            Attempt = Attempt,
            MaxAttempts = MaxAttempts,
            RunAt = RunAt,
            LastError = LastError,
        };
    }
}
