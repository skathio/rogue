# SkathIO.Rogue.WebApi.Integration.Tests

The **normative SRS-FR HTTP-boundary coverage gate** (PD-25) for SkathIO.Rogue.

This suite boots the Phase 7.2.1 host (`samples/SkathIO.Rogue.Sample.WebApi/`) via
`WebApplicationFactory<Program>` and asserts every SRS functional requirement that is observable at
the HTTP boundary. Every `[Fact]`/`[Theory]` carries a `// Covers: FR-xxx` tag. If an FR observable
over HTTP regresses, a test here must fail first.

## Host wiring (no static handler state)

- The host boots once per test class via `IClassFixture<WebApplicationFactory<Program>>`. No leaked
  state between tests.
- Handlers carry **no static mutable state**. Per-request observation uses the DI-scoped
  `IHandlerCallTracker` (declared in the host, `AddScoped`), read back via a resolved scope.
- This project **does not** add the generator as an analyzer reference. It declares zero handlers, so
  it consumes the host's generated registration (carried into the process by the host assembly's
  generator-emitted `[ModuleInitializer]`). Adding a second analyzer would emit a competing empty
  registration and a second module initializer that could clobber the host's populated registrar
  (last-writer-wins on `RogueRegistrationBridge.GeneratedRegistrar`). See the diary plan-change
  (2026-06-06).
- Strategy/lifetime variants that cannot be reached through the default host (parallel `WhenAll`
  publisher) use `WebApplicationFactory.WithWebHostBuilder()` to override the DI-registered
  `INotificationPublisher` in a derived factory, isolated to the test that needs it.

## FR → test-class map (Phase 7.3.1 — dispatch shapes)

| FR | What it asserts | Test class · test |
|----|------------------|-------------------|
| FR-1 | `IRequest<TResponse>` round-trips | `SendDispatchTests.Ping_RequestResponse_RoundTrips` |
| FR-2 | `IRequest` (no response) completes | `SendDispatchTests.SilentCommand_NoResponse_Completes` |
| FR-3 | `IQuery<T>` semantic alias dispatches | `SendDispatchTests.Query_SemanticAlias_Dispatches` |
| FR-7 | handler returns `ValueTask<TResponse>` | `SendDispatchTests.Ping_RequestResponse_RoundTrips` |
| FR-8 | no-response handler returns `ValueTask`; 204 | `SendDispatchTests.SilentCommand_NoResponse_Completes` |
| FR-9 | `IQueryHandler<,>` alias dispatches | `SendDispatchTests.Query_SemanticAlias_Dispatches` |
| FR-14 | `ISender.Send` and `CreateStream` are `CancellationToken`-aware | `SendDispatchTests.Send_CancelledToken_SurfacesCancellation`; `StreamTests.CreateStream_CancelledToken_SurfacesCancellation` |
| FR-16 | `IMediator` convenience type dispatches | `SendDispatchTests.Mediator_ConvenienceType_Dispatches` |
| FR-17 | object-typed `Send(object)` works; unknown type → defined exception | `SendDispatchTests.ObjectOverload_KnownType_Dispatches`, `…ObjectOverload_UnknownType_YieldsDefinedException` |
| FR-32 | a single `AddRogue(...)` wires the whole host | `SendDispatchTests.SingleAddRogue_WiresAllShapes` (+ implicit in every test) |
| FR-35 | configured lifetimes resolve; scoped service fresh per request scope | `SendDispatchTests.ScopedTracker_FreshPerRequestScope` |
| FR-4 | `INotification` dispatch via `IPublisher` | `PublishTests.Notify_FansOutToAllHandlers_Sequential` |
| FR-10 | `INotificationHandler<T>` handlers run | `PublishTests.Notify_FansOutToAllHandlers_Sequential` |
| FR-13 | zero handlers → `Publish` is a no-op | `PublishTests.Notify_ZeroHandlers_IsNoOp` |
| FR-15 | `IPublisher.Publish` is `CancellationToken`-aware | `PublishTests.Publish_CancelledToken_SurfacesCancellation` |
| FR-28 | both `ForeachAwait` and `WhenAll` fan out to all handlers | `PublishTests.Notify_FansOutToAllHandlers_Sequential`, `…_Parallel` |
| FR-29 | per-strategy error aggregation (first throw / `AggregateException`) | `PublishTests.Publish_Sequential_SurfacesFirstThrow`, `…Publish_Parallel_AggregatesAllThrows` |
| FR-5 | `IStreamRequest<T>` dispatch | `StreamTests.Stream_YieldsAllElements` |
| FR-11 | `IStreamRequestHandler<,>` yields `IAsyncEnumerable<T>` | `StreamTests.Stream_YieldsAllElements` |

## FR → test-class map (Phase 7.3.2 — pipeline behaviors)

| FR | What it asserts | Test class · test |
|----|------------------|-------------------|
| FR-19 | `IPipelineBehavior<,>` runs before and after the handler | `PipelineTests.Behavior_WrapsHandler_BeforeAndAfter` |
| FR-20 | open-generic behavior applies to all request types | `PipelineTests.OpenGenericBehavior_AppliesToAllRequestTypes` |
| FR-21 | behavior order is deterministic | `PipelineTests.Inspector_ReportsDeterministicBehaviorOrder` |
| FR-22 | short-circuiting behavior skips the handler | `PipelineTests.ShortCircuitingBehavior_SkipsHandler` |
| FR-23 | `IStreamPipelineBehavior<,>` wraps each stream item | `PipelineTests.StreamBehavior_WrapsEachYieldedItem` |
| FR-24 | behavior resolves constructor DI dependencies per scope | `PipelineTests.Behavior_ResolvesConstructorDependenciesPerScope` |
| FR-25 | pre/post processors run around the handler, in order | `PipelineTests.PrePostProcessors_RunAroundHandler_InOrder` |
| FR-26 | exception handler supplies fallback (returned at HTTP boundary); action observes without suppressing | `PipelineTests.ExceptionHandler_SuppliesFallback_ReturnedAtHttpBoundary`, `…ExceptionAction_ObservesWithoutSuppressing` |
| FR-27 | pre/post processors + behaviors run on the same single-registration engine | `PipelineTests.SingleEngine_ProcessorsAndBehaviors_RunTogether` |
| FR-36 | third-party behavior on public contracts works | `PipelineTests.ThirdPartyBehavior_OnPublicContract_Works` |
| FR-37 | `IRoguePipelineInspector` returns resolved behavior order | `PipelineTests.Inspector_ReportsDeterministicBehaviorOrder` |
| FR-38 | `LoggingBehavior` logs name + outcome + elapsed | `PipelineTests.LoggingBehavior_LogsNameAndOutcome` |
| FR-39 | `ValidationBehavior` → `ValidationException` → HTTP 400 | `PipelineTests.ValidationBehavior_FailedValidation_Returns400` |
| NFR-SEC-2 | `LoggingBehavior` does NOT log request/response payloads by default | `PipelineTests.NfrSec2_LoggingBehavior_LogsNameNotPayload` |

## PD-25 FR-ledger completeness table (FR-1 through FR-45)

Every SRS FR is accounted for below — either as an HTTP-boundary test or a named exclusion.

| FR | Category | Coverage |
|----|----------|----------|
| FR-1 | HTTP-boundary | `SendDispatchTests` |
| FR-2 | HTTP-boundary | `SendDispatchTests` |
| FR-3 | HTTP-boundary | `SendDispatchTests` |
| FR-4 | HTTP-boundary | `PublishTests` |
| FR-5 | HTTP-boundary | `StreamTests` |
| FR-6 | **compile-time-only** | Interface-only contracts; type-system + IDE enforcement; covered by unit tests. No HTTP-boundary assertion possible. |
| FR-7 | HTTP-boundary | `SendDispatchTests` |
| FR-8 | HTTP-boundary | `SendDispatchTests` |
| FR-9 | HTTP-boundary | `SendDispatchTests` |
| FR-10 | HTTP-boundary | `PublishTests` |
| FR-11 | HTTP-boundary | `StreamTests` |
| FR-12 | **compile-time-only** | Zero/multiple-handler errors are ROGUE001/002 diagnostics (compile-time); generator tests cover. |
| FR-13 | HTTP-boundary | `PublishTests.Notify_ZeroHandlers_IsNoOp` |
| FR-14 | HTTP-boundary | `SendDispatchTests.Send_CancelledToken_SurfacesCancellation` (Send path); `StreamTests.CreateStream_CancelledToken_SurfacesCancellation` (CreateStream path) |
| FR-15 | HTTP-boundary (container-boundary) | `PublishTests.Publish_CancelledToken_SurfacesCancellation` — no HTTP endpoint shape exists for this FR; tested at container boundary. |
| FR-16 | HTTP-boundary | `SendDispatchTests.Mediator_ConvenienceType_Dispatches` |
| FR-17 | HTTP-boundary (container-boundary) | `SendDispatchTests.ObjectOverload_UnknownType_YieldsDefinedException` — no HTTP endpoint for unknown-type path; tested at container boundary. |
| FR-18 | **compile-time-only** | ROGUE010 narrowest-interface nudge analyzer; compile-time only; covered by generator tests. |
| FR-19 | HTTP-boundary | `PipelineTests.Behavior_WrapsHandler_BeforeAndAfter` |
| FR-20 | HTTP-boundary | `PipelineTests.OpenGenericBehavior_AppliesToAllRequestTypes` |
| FR-21 | HTTP-boundary | `PipelineTests.Inspector_ReportsDeterministicBehaviorOrder` |
| FR-22 | HTTP-boundary | `PipelineTests.ShortCircuitingBehavior_SkipsHandler` |
| FR-23 | HTTP-boundary | `PipelineTests.StreamBehavior_WrapsEachYieldedItem` |
| FR-24 | HTTP-boundary | `PipelineTests.Behavior_ResolvesConstructorDependenciesPerScope` |
| FR-25 | HTTP-boundary | `PipelineTests.PrePostProcessors_RunAroundHandler_InOrder` — pre/post processors run around the handler in deterministic order (PD-29 resolved, Phase 7.4.1). |
| FR-26 | HTTP-boundary | `PipelineTests.ExceptionHandler_SuppliesFallback_ReturnedAtHttpBoundary` — exception handler fallback returned at the HTTP boundary (drives `/faulting-request` via `client.PostAsJsonAsync`) (PD-29 resolved). |
| FR-26 | HTTP-boundary (container-boundary) | `PipelineTests.ExceptionAction_ObservesWithoutSuppressing` — drives `sender.Send` inside a DI scope; an observe-only action has no response-shape difference an HTTP assertion could see, so the scoped tracker is the only way to observe it (matches the FR-15/FR-17 precedent) (PD-29 resolved). |
| FR-27 | HTTP-boundary | `PipelineTests.SingleEngine_ProcessorsAndBehaviors_RunTogether` — processors + behaviors run on one engine wired by a single `AddRogue()` (PD-29 resolved). |
| FR-28 | HTTP-boundary | `PublishTests.Notify_FansOutToAllHandlers_Parallel` |
| FR-29 | HTTP-boundary | `PublishTests.Publish_Sequential_SurfacesFirstThrow`, `Publish_Parallel_AggregatesAllThrows` |
| FR-30 | **compile-time-only** | ROGUE diagnostic; generator tests cover. |
| FR-31 | **compile-time-only** | ROGUE diagnostic; generator tests cover. |
| FR-32 | HTTP-boundary | Implicit — all tests pass against a single `AddRogue(...)` registration. |
| FR-33 | **compile-time-only** | ROGUE diagnostic; generator tests cover. |
| FR-34 | **compile-time-only** | ROGUE diagnostic; generator tests cover. |
| FR-35 | HTTP-boundary | `SendDispatchTests.ScopedTracker_FreshPerRequestScope` |
| FR-36 | HTTP-boundary | `PipelineTests.ThirdPartyBehavior_OnPublicContract_Works` |
| FR-37 | HTTP-boundary | `PipelineTests.Inspector_ReportsDeterministicBehaviorOrder` |
| FR-38 | HTTP-boundary | `PipelineTests.LoggingBehavior_LogsNameAndOutcome` |
| FR-39 | HTTP-boundary | `PipelineTests.ValidationBehavior_FailedValidation_Returns400` |
| FR-40 | **migration-tooling-only** | Covered by `SkathIO.Rogue.Migration.Tests` (Phase 6). |
| FR-41 | **migration-tooling-only** | Covered by `SkathIO.Rogue.Migration.Tests` (Phase 6). |
| FR-42 | **migration-tooling-only** | Covered by `SkathIO.Rogue.Migration.Tests` (Phase 6). |
| FR-43 | **migration-tooling-only** | Covered by `SkathIO.Rogue.Migration.Tests` (Phase 6). |
| FR-44 | **migration-tooling-only** | Covered by `SkathIO.Rogue.Migration.Tests` (Phase 6). |
| FR-45 | **Met** (telemetry-wired) | OTel `ActivitySource`/`Meter` shim built in Phase 5.1 and **genuinely invoked** by the generated dispatcher as of Phase 9.2 (`StartDispatch`/`StopDispatch` emitted on `Send_X`/`Publish_X`/`CreateStream_X`). Two end-to-end `ActivityListener` tests in `tests/SkathIO.Rogue.Behaviors.Tests/TelemetryTests.cs` (`Send_EmitsActivity_WhenTelemetryEnabled`, `Send_EmitsErrorOutcome_DoesNotLeakExceptionMessage`) observe the real `rogue.dispatch` activity over a real DI + generated-dispatcher path. The WAF HTTP-boundary assertion remains deliberately omitted (a "Should" NFR adds WAF test surface without gating v1), but FR-45 is no longer "dead code"/"deferred" — it is covered by behavior-tests-level e2e assertions. |

### Named exclusion categories

| Category | Meaning |
|----------|---------|
| **compile-time-only** | The FR is enforced by the compiler / Roslyn analyzer / type system — no HTTP-boundary assertion is possible or meaningful. Covered by unit tests or generator tests. |
| **migration-tooling-only** | The FR is in the MediatR→Rogue migration tooling (Phase 6). Covered by `SkathIO.Rogue.Migration.Tests`. |
| **post-v1 deferred** | The feature is built but its WAF HTTP-boundary assertion is deliberately deferred. The reason is stated. The test surface is tracked as a follow-up. |
| **registration-only (PD-29)** | *Resolved — no longer used.* This category previously covered FR-25/26/27 while the processor-bridge runtime invocation was unimplemented. Phase 7.4.1 closed the gap (PD-29 resolved); FR-25/26/27 are now plain `HTTP-boundary` rows with runtime-behavior assertions. Retained here for historical context. |

### Regression contract

Any FR regression observable at the HTTP boundary MUST surface as a test failure in this suite first.
If a test in this suite fails in CI without a corresponding code change, investigate the host
(`samples/SkathIO.Rogue.Sample.WebApi/`) or the generator before the library sources.
