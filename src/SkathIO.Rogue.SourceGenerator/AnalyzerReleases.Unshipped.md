### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
ROGUE001 | SkathIO.Rogue | Error | No handler registered for command/query type
ROGUE002 | SkathIO.Rogue | Error | Multiple handlers registered for request type
ROGUE003 | SkathIO.Rogue | Error | Handler response type does not match request
ROGUE004 | SkathIO.Rogue | Warning | Handler may not be constructable
ROGUE005 | SkathIO.Rogue | Error | Handler or behavior is abstract or has no public constructor
ROGUE006 | SkathIO.Rogue | Warning | Open-generic request type not supported by generator
ROGUE010 | SkathIO.Rogue | Info | Consider injecting ISender or IPublisher instead of IMediator
ROGUE011 | SkathIO.Rogue | Error | Type implements multiple CQS contracts
ROGUE012 | SkathIO.Rogue | Warning | Adapter command-vs-query mapping conflict
ROGUE013 | SkathIO.Rogue | Warning | Feature package version differs from the core package
ROGUE014 | SkathIO.Rogue | Error | [Validate] type must be partial
ROGUE015 | SkathIO.Rogue | Error | Validation rule does not fit the property
ROGUE016 | SkathIO.Rogue | Error | Rules hook has the wrong signature
ROGUE017 | SkathIO.Rogue | Error | Validated type has no generated validator
ROGUE018 | SkathIO.Rogue | Warning | Validation rules never run
