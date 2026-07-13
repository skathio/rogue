namespace SkathIO.Rogue;

/// <summary>
/// Combines <see cref="ISender"/> and <see cref="IPublisher"/>. Prefer injecting the narrower interface.
/// <para>
/// Same scoped-dispatch requirement as <see cref="ISender"/> applies here: resolve from a scope
/// (a request-bound scope, or one created explicitly via <c>IServiceScopeFactory.CreateScope()</c>),
/// not the root <c>IServiceProvider</c> — see <see cref="ISender"/>'s remarks and the README's
/// "Scoped dispatch" section.
/// </para>
/// </summary>
public interface IMediator : ISender, IPublisher { }
