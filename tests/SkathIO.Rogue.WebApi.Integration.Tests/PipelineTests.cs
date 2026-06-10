using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkathIO.Rogue.Sample.WebApi;
using Xunit;

namespace SkathIO.Rogue.WebApi.Integration.Tests;

/// <summary>
/// HTTP-boundary + container-boundary coverage for the pipeline-behavior contracts (FR-19–27,
/// FR-36–39) and the NFR-SEC-2 no-payload-logging guarantee. Boots the 7.2.1 host once per class via
/// <see cref="IClassFixture{T}"/>; behaviors woven onto every request are the package-provided
/// <c>LoggingBehavior</c> + <c>ValidationBehavior</c> (host <c>AddOpenBehavior</c>) plus the
/// host-declared closed <see cref="TrackingStreamBehavior"/> on the stream path. Variants that need
/// a behavior the default host does not weave (an always-failing validator) use a derived factory.
///
/// FR-25/26/27 (pre/post processors + exception handlers) are now runtime-functional (PD-29 resolved,
/// Phase 7.4.1): the generated dispatch loop resolves and invokes registered pre/post processors
/// around the behavior pipeline and runs exception handlers/actions on a thrown exception. The
/// processor side effects are observed via the DI-scoped <see cref="IHandlerCallTracker"/>; the
/// exception-handler fallback response is observed at the HTTP boundary.
/// </summary>
public sealed class PipelineTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public PipelineTests(WebApplicationFactory<Program> factory) => _factory = factory;

    // Covers: FR-45 — telemetry is wired into the generated dispatcher (Phase 9.2) and asserted
    // end-to-end by two ActivityListener tests in TelemetryTests (SkathIO.Rogue.Behaviors.Tests):
    // Send_EmitsActivity_WhenTelemetryEnabled / Send_EmitsErrorOutcome_DoesNotLeakExceptionMessage.
    // Only the WAF HTTP-boundary assertion stays deferred (a Should NFR that does not gate v1); the
    // FR itself is no longer "deferred"/"dead code" — see the FR-ledger row in this project's README.

    // Covers: FR-19 — an IPipelineBehavior wraps the handler: it runs BEFORE and AFTER it. The
    // package LoggingBehavior is woven onto every request; a single dispatch emits both a
    // "dispatching" (pre-next) and a "dispatched ... success" (post-next) log line for the same
    // request, proving the behavior's Handle ran around the handler.
    [Fact]
    public async Task Behavior_WrapsHandler_BeforeAndAfter()
    {
        using var captured = new CapturingFactory(_factory);
        var client = captured.CreateClient();

        var response = await client.PostAsJsonAsync("/ping", new PingRequest("wrap"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lines = captured.Logs.Messages(nameof(PingRequest));
        Assert.Contains(lines, m => m.Contains("dispatching", StringComparison.Ordinal));
        Assert.Contains(lines, m => m.Contains("success", StringComparison.Ordinal));
    }

    // Covers: FR-20 — one open-generic behavior registration applies to ALL request types. The
    // open-generic LoggingBehavior<,> is registered once (host AddOpenBehavior) yet runs for two
    // distinct request types: PingRequest and GetItemQuery.
    [Fact]
    public async Task OpenGenericBehavior_AppliesToAllRequestTypes()
    {
        using var captured = new CapturingFactory(_factory);
        var client = captured.CreateClient();

        await client.PostAsJsonAsync("/ping", new PingRequest("a"));
        await client.GetAsync("/query/5");

        Assert.NotEmpty(captured.Logs.Messages(nameof(PingRequest)));
        Assert.NotEmpty(captured.Logs.Messages(nameof(GetItemQuery)));
    }

    // Covers: FR-21 — behavior order is deterministic and inspectable. The host wires LoggingBehavior
    // then ValidationBehavior (both order 0; PD-13a tie-break is FQN-lexicographic), so the resolved
    // order is stable across runs. Asserted via IRoguePipelineInspector resolved from the container.
    // Covers: FR-37 — IRoguePipelineInspector.GetPipeline<TRequest>() returns the resolved order.
    [Fact]
    public void Inspector_ReportsDeterministicBehaviorOrder()
    {
        using var scope = _factory.Services.CreateScope();
        var inspector = scope.ServiceProvider.GetRequiredService<IRoguePipelineInspector>();

        var pipeline = inspector.GetPipeline<PingRequest>();

        Assert.Equal(2, pipeline.Count);
        // PD-13a: equal [BehaviorOrder] → source-before-metadata → FQN lexicographic.
        // "LoggingBehavior" sorts before "ValidationBehavior".
        Assert.Contains("LoggingBehavior", pipeline[0].BehaviorType.Name, StringComparison.Ordinal);
        Assert.Contains("ValidationBehavior", pipeline[1].BehaviorType.Name, StringComparison.Ordinal);
        Assert.True(pipeline[0].Order <= pipeline[1].Order);
    }

    // Covers: FR-22 — a behavior short-circuits (returns/throws without calling next) and the handler
    // never runs. ValidationBehavior short-circuits BEFORE next() when a validator fails; with an
    // always-failing validator the PingHandler never produces its 200 response — the boundary returns
    // 400 instead, proving the handler was skipped.
    [Fact]
    public async Task ShortCircuitingBehavior_SkipsHandler()
    {
        using var failing = WithAlwaysFailingValidator(_factory);
        var client = failing.CreateClient();

        var response = await client.PostAsJsonAsync("/ping", new PingRequest("short"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Covers: FR-23 — IStreamPipelineBehavior<,> wraps a stream request (Phase 4.2.1 weaving). The
    // host-declared TrackingStreamBehavior records once on entry and once per yielded element. Driven
    // through the SAME generated CreateStream path the HTTP endpoint uses, in a dedicated scope so the
    // scoped tracker is readable post-enumeration.
    [Fact]
    public async Task StreamBehavior_WrapsEachYieldedItem()
    {
        using var scope = _factory.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var tracker = scope.ServiceProvider.GetRequiredService<IHandlerCallTracker>();

        var items = new List<int>();
        await foreach (int item in sender.CreateStream(new NumberStreamRequest(3)))
        {
            items.Add(item);
        }

        Assert.Equal(new[] { 0, 1, 2 }, items);
        Assert.Equal(1, tracker.Calls.Count(c => c == TrackingStreamBehavior.Wrapped));
        Assert.Equal(3, tracker.Calls.Count(c => c == TrackingStreamBehavior.PerItem));
    }

    // Covers: FR-24 — a behavior resolves its own constructor DI dependencies at request scope.
    // LoggingBehavior<,> injects ILogger<LoggingBehavior<,>>; TrackingStreamBehavior injects the
    // scoped IHandlerCallTracker. Both resolve as part of the dispatch — proven by the stream behavior
    // recording into the per-scope tracker (constructor-injected) and the logging behavior emitting.
    [Fact]
    public async Task Behavior_ResolvesConstructorDependenciesPerScope()
    {
        using var scope = _factory.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var tracker = scope.ServiceProvider.GetRequiredService<IHandlerCallTracker>();

        await foreach (int _ in sender.CreateStream(new NumberStreamRequest(1))) { }

        // The behavior could only record if its ctor dependency (this scope's tracker) was injected.
        Assert.Contains(TrackingStreamBehavior.Wrapped, tracker.Calls);
    }

    // Covers: FR-25 — registered IRequestPreProcessor<T> runs before the behavior pipeline and
    // IRequestPostProcessor<T,R> runs after, in deterministic order, through the generated dispatch
    // loop (PD-29 resolved). Observed via the DI-scoped IHandlerCallTracker: the pre-processor,
    // handler, and post-processor each record a marker, and the recorded order is pre → handler → post.
    [Fact]
    public async Task PrePostProcessors_RunAroundHandler_InOrder()
    {
        using var scope = _factory.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var tracker = scope.ServiceProvider.GetRequiredService<IHandlerCallTracker>();

        var response = await sender.Send(new ProcessedRequest("v"));

        Assert.Equal("v", response.Value);
        Assert.Equal(
            new[] { RecordingPreProcessor.Marker, ProcessedRequestHandler.Marker, RecordingPostProcessor.Marker },
            tracker.Calls);
    }

    // Covers: FR-26 — when the handler throws, the registered IRequestExceptionHandler<T,R,TEx> marks
    // the exception handled and supplies a fallback response, which the dispatch loop returns instead
    // of propagating. Asserted at the HTTP boundary: the faulting endpoint returns 200 with the
    // fallback body rather than mapping the exception to 500.
    [Fact]
    public async Task ExceptionHandler_SuppliesFallback_ReturnedAtHttpBoundary()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/faulting-request", new FaultingRequest("v"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<FaultingResponse>();
        Assert.NotNull(body);
        Assert.Equal(FaultingRequestExceptionHandler.FallbackValue, body!.Value);
    }

    // Covers: FR-26 — IRequestExceptionAction<T,TEx> observes the exception but does NOT suppress
    // propagation (the exception handler does that). The action records into the scoped tracker; the
    // dispatch still returns the handler's fallback. Driven in-scope so the scoped tracker is readable.
    [Fact]
    public async Task ExceptionAction_ObservesWithoutSuppressing()
    {
        using var scope = _factory.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var tracker = scope.ServiceProvider.GetRequiredService<IHandlerCallTracker>();

        var response = await sender.Send(new FaultingRequest("v"));

        // The exception handler still supplied the fallback (action did not suppress).
        Assert.Equal(FaultingRequestExceptionHandler.FallbackValue, response.Value);
        // The observe-only action ran.
        Assert.Contains(FaultingRequestExceptionAction.Marker, tracker.Calls);
    }

    // Covers: FR-27 — pre/post processors AND the behavior pipeline run through the SAME engine wired
    // by a single AddRogue(). The open-generic LoggingBehavior (woven onto every request) emits its
    // log lines for ProcessedRequest in the SAME dispatch that runs the pre/post processors, proving
    // there is no second/competing pipeline — one registration path drives both.
    [Fact]
    public async Task SingleEngine_ProcessorsAndBehaviors_RunTogether()
    {
        using var captured = new CapturingFactory(_factory);

        // Dispatch in-scope so the scoped tracker (processor markers) is readable, AND capture logs so
        // the behavior (LoggingBehavior) running in the same dispatch is observable.
        using var scope = captured.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var scopeTracker = scope.ServiceProvider.GetRequiredService<IHandlerCallTracker>();

        await sender.Send(new ProcessedRequest("v"));

        // Pre/post processors ran (engine invoked them) …
        Assert.Contains(RecordingPreProcessor.Marker, scopeTracker.Calls);
        Assert.Contains(RecordingPostProcessor.Marker, scopeTracker.Calls);
        // … and the open-generic behavior ran in the same single-registration pipeline.
        Assert.NotEmpty(captured.Logs.Messages(nameof(ProcessedRequest)));
    }

    // Covers: FR-36 — a third-party behavior built only on the public IPipelineBehavior<,> contract
    // works without special ceremony. LoggingBehavior and ValidationBehavior live in SEPARATE packages
    // (SkathIO.Rogue.Logging / SkathIO.Rogue.Validation.FluentValidation), implement only the public
    // IPipelineBehavior<,> contract, and are woven into the host pipeline by a plain AddOpenBehavior.
    [Fact]
    public void ThirdPartyBehavior_OnPublicContract_Works()
    {
        using var scope = _factory.Services.CreateScope();
        var inspector = scope.ServiceProvider.GetRequiredService<IRoguePipelineInspector>();

        var names = inspector.GetPipeline<PingRequest>().Select(b => b.BehaviorType.Name).ToList();

        Assert.Contains(names, n => n.Contains("LoggingBehavior", StringComparison.Ordinal));
        Assert.Contains(names, n => n.Contains("ValidationBehavior", StringComparison.Ordinal));
    }

    // Covers: FR-38 — LoggingBehavior logs the request name and the success outcome + elapsed time.
    [Fact]
    public async Task LoggingBehavior_LogsNameAndOutcome()
    {
        using var captured = new CapturingFactory(_factory);
        var client = captured.CreateClient();

        await client.PostAsJsonAsync("/ping", new PingRequest("log"));

        var lines = captured.Logs.Messages(nameof(PingRequest));
        Assert.NotEmpty(lines);
        Assert.Contains(lines, m => m.Contains("success", StringComparison.Ordinal) && m.Contains("ms", StringComparison.Ordinal));
    }

    // Covers: FR-39 — ValidationBehavior short-circuits on validation failure with a
    // ValidationException, mapped to HTTP 400 at the boundary by the host's exception handling.
    [Fact]
    public async Task ValidationBehavior_FailedValidation_Returns400()
    {
        using var failing = WithAlwaysFailingValidator(_factory);
        var client = failing.CreateClient();

        var response = await client.PostAsJsonAsync("/ping", new PingRequest("invalid"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Covers: NFR-SEC-2 — the default LoggingBehavior logs the request NAME but NEVER the request or
    // response PAYLOAD. The host uses default LoggingOptions (LogPayload off) and PingRequest carries
    // no [LogPayload], so the captured output must contain "PingRequest" but not the field values.
    [Fact]
    public async Task NfrSec2_LoggingBehavior_LogsNameNotPayload()
    {
        const string secret = "super-secret-payload-value";
        using var captured = new CapturingFactory(_factory);
        var client = captured.CreateClient();

        var response = await client.PostAsJsonAsync("/ping", new PingRequest(secret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var all = captured.Logs.All();
        Assert.Contains(all, m => m.Contains(nameof(PingRequest), StringComparison.Ordinal));
        // The payload (request field value, echoed into the response) must NOT appear in any log line.
        Assert.DoesNotContain(all, m => m.Contains(secret, StringComparison.Ordinal));
    }

    // ── Test infrastructure ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A derived factory that injects an in-memory <see cref="ILoggerProvider"/> so a test can read
    /// back every log line the host emitted during a request (FR-38 / NFR-SEC-2). Disposable so the
    /// captured buffer does not leak across tests.
    /// </summary>
    private sealed class CapturingFactory : IDisposable
    {
        private readonly WebApplicationFactory<Program> _derived;

        internal CapturingFactory(WebApplicationFactory<Program> root)
        {
            Logs = new CapturedLogs();
            _derived = root.WithWebHostBuilder(b =>
                b.ConfigureServices(s => s.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(Logs))));
        }

        internal CapturedLogs Logs { get; }

        internal System.Net.Http.HttpClient CreateClient() => _derived.CreateClient();

        /// <summary>The derived host's root service provider (for in-scope dispatch + log capture).</summary>
        internal IServiceProvider Services => _derived.Services;

        public void Dispose() => _derived.Dispose();
    }

    /// <summary>Derived factory registering an always-failing validator for <see cref="PingRequest"/>.</summary>
    private static WebApplicationFactory<Program> WithAlwaysFailingValidator(WebApplicationFactory<Program> root) =>
        root.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<IValidator<PingRequest>>(new AlwaysFailValidator())));

    private sealed class AlwaysFailValidator : IValidator<PingRequest>
    {
        private static readonly ValidationResult Failure =
            new(new[] { new ValidationFailure(nameof(PingRequest.Message), "always invalid") });

        public ValidationResult Validate(IValidationContext context) => Failure;

        public Task<ValidationResult> ValidateAsync(IValidationContext context, CancellationToken cancellation = default) =>
            Task.FromResult(Failure);

        public ValidationResult Validate(PingRequest instance) => Failure;

        public Task<ValidationResult> ValidateAsync(PingRequest instance, CancellationToken cancellation = default) =>
            Task.FromResult(Failure);

        public global::FluentValidation.IValidatorDescriptor CreateDescriptor() =>
            throw new NotSupportedException("Test stub.");

        public bool CanValidateInstancesOfType(Type type) => type == typeof(PingRequest);
    }

    private sealed class CapturedLogs
    {
        private readonly ConcurrentQueue<string> _messages = new();

        internal void Add(string message) => _messages.Enqueue(message);

        internal IReadOnlyList<string> All() => _messages.ToArray();

        internal IReadOnlyList<string> Messages(string requestName) =>
            _messages.Where(m => m.Contains(requestName, StringComparison.Ordinal)).ToArray();
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly CapturedLogs _logs;

        internal CapturingLoggerProvider(CapturedLogs logs) => _logs = logs;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_logs);

        public void Dispose() { }

        private sealed class CapturingLogger : ILogger
        {
            private readonly CapturedLogs _logs;

            internal CapturingLogger(CapturedLogs logs) => _logs = logs;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                _logs.Add(formatter(state, exception));
        }
    }
}
