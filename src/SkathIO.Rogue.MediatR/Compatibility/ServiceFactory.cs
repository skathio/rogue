namespace SkathIO.Rogue.Compatibility;

// MediatR's ServiceFactory delegate — retained for signature compatibility.
// Not used by Rogue's DI-resolved dispatch; provided so user code that references
// ServiceFactory compiles without changes during migration.
public delegate object ServiceFactory(System.Type serviceType);
