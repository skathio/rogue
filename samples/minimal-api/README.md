# SkathIO.Rogue — minimal API quickstart

A complete, runnable greeting endpoint backed by SkathIO.Rogue. Request in, response out, dispatched
through a compile-time-generated mediator — no reflection, no assembly scanning, AOT-safe.

## Run it (≤ 5 minutes from a clean clone)

```bash
dotnet run --project samples/minimal-api
```

Then, in another terminal:

```bash
curl "http://localhost:5000/greet/world"
# {"greeting":"Hello, world!"}
```

That is the entire round trip. Three things make it work, all in
[`Program.cs`](./Program.cs):

1. **A request + handler.**

   ```csharp
   public sealed record GreetRequest(string Name) : IRequest<GreetResponse>;
   public sealed record GreetResponse(string Greeting);

   public sealed class GreetHandler : IRequestHandler<GreetRequest, GreetResponse>
   {
       public ValueTask<GreetResponse> Handle(GreetRequest request, CancellationToken ct)
           => new(new GreetResponse($"Hello, {request.Name}!"));
   }
   ```

2. **One registration call.** `builder.Services.AddRogue();` — the source generator has already
   discovered `GreetHandler` at build time and emitted the wiring; `AddRogue()` just registers it.

3. **Dispatch through `ISender`.** `await sender.Send(new GreetRequest(name))` routes to the handler
   via a generated `switch`, not reflection.

## Starting from your own project

In a real project you reference the NuGet package instead of the project, and the generator ships
inside it as an analyzer asset — no extra wiring:

```bash
dotnet add package SkathIO.Rogue
```

```csharp
builder.Services.AddRogue();
```

This sample uses a project reference, so it also adds an explicit
`OutputItemType="Analyzer"` reference to `SkathIO.Rogue.SourceGenerator` (see the `.csproj`) to make
the generator run. Package consumers do **not** need that line — the analyzer flows from the package.

## Next steps

- [Getting started](../../docs/getting-started.md) — install, register, dispatch, troubleshoot.
- [Behaviors guide](../../docs/behaviors.md) — pipeline behaviors, ordering, logging, validation.
- [API reference](../../docs/api-reference.md) — the full public surface.
