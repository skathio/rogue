using SkathIO.Rogue;

namespace SkathIO.Rogue.Sample.WebApi;

/// <summary>
/// A notification with <b>no</b> registered handler. Publishing it must be a no-op (FR-13):
/// <c>Publish</c> completes successfully without dispatching anything.
/// </summary>
public sealed record UnhandledNotification(int Id) : IEvent;
