### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
ROGM001 | Migration | Warning | Replace MediatR using directive
ROGM002 | Migration | Warning | Replace Task return type with ValueTask
ROGM003 | Migration | Info | Open-generic request requires manual migration
ROGM004 | Migration | Info | AddMediatR is forwarded to AddRogue
ROGM005 | Migration | Warning | Ambiguous command-vs-query intent — migrated to ICommand, review manually
ROGM006 | Migration | Warning | Migrate MediatR marker/handler interface to the CQS contract
