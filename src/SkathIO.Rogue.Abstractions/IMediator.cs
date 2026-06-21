namespace SkathIO.Rogue;

/// <summary>Combines <see cref="ISender"/> and <see cref="IPublisher"/>. Prefer injecting the narrower interface.</summary>
public interface IMediator : ISender, IPublisher { }
