# SkathIO.Rogue.WebApi.Integration.Tests

End-to-end HTTP-boundary integration tests for SkathIO.Rogue. The suite boots the
[`SkathIO.Rogue.Sample.WebApi`](../../samples/SkathIO.Rogue.Sample.WebApi/) host via
`WebApplicationFactory<Program>` and asserts the library's observable behavior through real HTTP
requests — send/response, void commands, queries, notification fan-out, streaming, pipeline behaviors,
pre/post processors and exception handlers, logging, and validation.

## Host wiring

- The host boots once per test class via `IClassFixture<WebApplicationFactory<Program>>`; no state
  leaks between tests.
- Handlers carry **no static mutable state**. Per-request observation uses the DI-scoped
  `IHandlerCallTracker` (registered `AddScoped` in the host), read back through a resolved scope.
- This project declares zero handlers and does **not** add the generator as an analyzer reference — it
  consumes the host's generated registration (carried into the process by the host assembly's
  generator-emitted `[ModuleInitializer]`). Adding a second analyzer would emit a competing empty
  registration.
- Variants that can't be reached through the default host (e.g. the parallel `WhenAll` publisher) use
  `WebApplicationFactory.WithWebHostBuilder()` to override the registered publisher in a derived
  factory, isolated to the test that needs it.

## Test classes

| Class | Covers |
|-------|--------|
| `SendDispatchTests` | `Send`/`CreateStream` dispatch: request/response, void commands, queries, `IMediator`, object dispatch, cancellation, scoped lifetimes |
| `PublishTests` | Notification fan-out: sequential vs parallel publishers, zero-handler no-op, error aggregation, cancellation |
| `StreamTests` | `IAsyncEnumerable<T>` streaming and stream cancellation |
| `PipelineTests` | Pipeline behaviors (ordering, short-circuit, open-generic), stream behaviors, pre/post processors, exception handlers, logging, validation |

Any behavior change observable over HTTP should surface as a failure here first; if a test fails in CI
without a corresponding code change, investigate the host or the generator before the library sources.
