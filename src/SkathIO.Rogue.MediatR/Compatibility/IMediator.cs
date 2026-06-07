namespace SkathIO.Rogue.Compatibility;

// IMediator in MediatR extends ISender + IPublisher.
// Forwarded to Rogue's IMediator which already extends ISender + IPublisher.
public interface IMediator : global::SkathIO.Rogue.IMediator { }
