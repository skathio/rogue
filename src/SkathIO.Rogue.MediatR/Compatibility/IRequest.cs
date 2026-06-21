namespace SkathIO.Rogue.Compatibility;

// MediatR-shaped request markers. Self-contained (PD-46/PD-48): they do NOT extend any core marker —
// the core deleted IRequest entirely under the D5 clean break (PD-40), and unlike the notification and
// streaming adapter surfaces (PD-48 thin "is-a" sub-interfaces of IEvent/IStreamQuery<T>) the request
// surface genuinely CANNOT be an "is-a" of a single core contract: the F8 convention maps an unmarked
// IRequest<T> to ICommand<T> but a [MapAsQuery]-marked one to IQuery<T> — a per-type fork the type
// system cannot express. The adapter is the sole home of this MediatR-shaped surface (PD-43).
//
// PD-48 (11.4): the generator's adapter-mapping discovery rule (RogueGenerator.ExtractFromSymbol,
// WellKnownTypeNames.AdapterIRequest*) discovers handlers implementing the adapter IRequestHandler<,>/<>
// and maps them onto the CQS dispatch path per F8 ([MapAsQuery] -> IQuery, default -> ICommand),
// registering/resolving against the adapter handler interface and dispatching via SendObject. A bare
// IRequest<T> message type (no handler in the compilation) is itself not dispatchable through the typed
// ISender.Send overloads — call it via IMediator.Send(object) or the generated SendObject path.
public interface IRequest<out TResponse> { }
public interface IRequest : IRequest<global::SkathIO.Rogue.Unit> { }
