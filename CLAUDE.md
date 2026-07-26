# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Status

**Phase 0 (Connector Mechanism Spike) is complete.** Both unproven Connector mechanisms — negotiate interception and inbound dispatch over a synthetic connection — are confirmed working against .NET 10, with no fallback needed. 22 automated tests pass plus a real out-of-process `@microsoft/signalr` client check. Full results: [docs/docs/00-review-findings.md § Phase 0 Spike Results](docs/docs/00-review-findings.md#phase-0-spike-results-2026-07-26), [spike/findings/](spike/findings/).

Two real design-doc defects were found and fixed by tests that exercised the design doc's own claims rather than by review — see [spike/findings/inbound-dispatch-corrections.md](spike/findings/inbound-dispatch-corrections.md) and the corresponding corrections already applied to [docs/docs/04-design.md §11](docs/docs/04-design.md#11-connector--inbound-dispatch-synthetic-client-connections).

**Phase 1 (Core Service MVP) has not started.** There is still no `Switchboard.sln`, no `src/`, no `tests/` — only `spike/` (Phase 0's throwaway host + the carry-forward Connector skeleton) and `docs/`/`plans/`. Phase 1 promotes `spike/Phase0.Spike.Connector/*` into the real `Keryhe.Switchboard.Connector` project (swapping the stub redirect target for the real proxy-forwarding call) and retires `spike/Phase0.Spike.Host`. See [docs/docs/06-project-plan.md § Phase 1](docs/docs/06-project-plan.md).

> **Update this file after each phase completes.** When a phase (Phase 0, Phase 1, ...) is finished, revise "Project Status" to name the new current phase, and update any other section here that the completed phase changed (solution layout once scaffolded, architecture notes if implementation diverged from the design docs, etc.).

## What This Project Is

Switchboard is a self-hosted connection proxy and scale-out backplane for ASP.NET Core SignalR, written in .NET 10 C#. It solves the two classic SignalR scale-out problems without a cloud dependency:

1. **Connection offloading** — clients connect to Switchboard, not to app servers. App servers hold a small fixed pool of WebSocket connections to Switchboard (default 5 per hub) regardless of client count.
2. **Backplane** — because every client connects to Switchboard, it has a complete view of all connections and can fan out broadcasts/group/user messages across app servers with no sticky sessions.

Full rationale and comparison to Redis-backplane/Azure SignalR Service alternatives: [docs/docs/01-overview.md](docs/docs/01-overview.md).

## Commands

Phase 1's `Switchboard.sln` doesn't exist yet — the commands below are the expected shape once it's scaffolded (per the project plan):

```bash
dotnet build Switchboard.sln
dotnet test tests/Keryhe.Switchboard.UnitTests
dotnet test tests/Keryhe.Switchboard.IntegrationTests
dotnet test --filter "FullyQualifiedName~SomeTestClass.SomeTestMethod"   # single test
```

The Phase 0 spike solution is real and working today:

```bash
dotnet build spike/Phase0.Spike.slnx
dotnet test spike/Phase0.Spike.Tests/Phase0.Spike.Tests.csproj                              # all 22 tests
dotnet test spike/Phase0.Spike.Tests/Phase0.Spike.Tests.csproj --filter "FullyQualifiedName~SomeTestClass.SomeTestMethod"   # single test

# JS-client redirect check (A5) — needs the host running separately first:
dotnet run --project spike/Phase0.Spike.Host --no-build --urls http://localhost:5559 &
node spike/Phase0.Spike.JsClient/redirect-check.mjs http://localhost:5559
```

Note: the A5 `.NET`-client test (`DotNetClientEndToEndTests`) spawns `Phase0.Spike.Host.dll` as a real out-of-process Kestrel server on port 5559 — build the solution first (`dotnet build`) so that assembly exists before running the full test suite.

There is no linter configured in the repo yet — do not invent one.

## Architecture (target design — not yet implemented)

### Two-hop negotiate, then a persistent client transport

SignalR clients are **unmodified** (full wire compatibility is a hard requirement — [ADR-005](docs/docs/07-adr/ADR-005-protocol-compatibility.md)). The negotiate flow is a two-step redirect:

1. Client negotiates with the app server → app server forwards to Switchboard's negotiate endpoint with the user's identity → Switchboard returns a redirect `{url (https), accessToken}` (a short-lived JWT binding `connectionId` + `hubName` + optional `sub`).
2. Client re-negotiates against that `url` with the token → Switchboard returns `{connectionId, connectionToken, availableTransports}`. `connectionToken` is a distinct, opaque handle used only as the transport `id` — never confuse it with `connectionId`, which is the public identity used everywhere else (hub code, groups, management API).
3. Client opens the transport (WebSocket preferred; SSE and Long Polling also supported) using `connectionToken`.

Full spec: [docs/docs/03-protocol.md](docs/docs/03-protocol.md) Part 1. Sequence diagrams: [docs/docs/02-architecture.md](docs/docs/02-architecture.md).

### The Connector is the hard part — two unproven mechanisms

App servers never see real client connections. `Keryhe.Switchboard.Connector` (added to the app server as a NuGet package via `AddSwitchboardConnector()`) has to fake both directions:

- **Outbound divert (negotiate interception):** `MapHub<T>()` handles negotiate inline in the framework with no DI seam. The Connector registers a `MatcherPolicy` + `IEndpointSelectorPolicy` that detects the negotiate endpoint and replaces its `RequestDelegate` with one that returns Switchboard's redirect — while preserving the original endpoint's `Metadata` (this is the *only* way class-level `[Authorize]` on a Hub survives, since `DefaultHubDispatcher` doesn't enforce it). See [docs/docs/04-design.md §8](docs/docs/04-design.md).
- **Inbound dispatch (synthetic connections):** since there's no real `ConnectionContext` for app-server-side clients, the Connector builds `HubConnectionHandler<THub>` via `ConnectionBuilder` once per hub type, then drives it per logical client with a synthetic `ConnectionContext` backed by a `Pipe` pair (`SwitchboardClientConnectionContext`). Identity flows in only through `IConnectionUserFeature`. `Context.GetHttpContext()` is always `null` on these synthetic connections — a documented, permanent incompatibility. See [docs/docs/04-design.md §11](docs/docs/04-design.md).

Both mechanisms were "designed against framework internals rather than exercised in code" — that's why Phase 0 existed before Phase 1 was planned, and both are now confirmed working in `spike/Phase0.Spike.Connector/` with no fallback needed. See [docs/docs/00-review-findings.md](docs/docs/00-review-findings.md) for the history (a previous DI-override approach to negotiate interception turned out to be a silent no-op) and the Phase 0 results. Two subtleties the spike surfaced, already fixed in the spike code and reflected in the design doc: identity reconstruction must only set a non-null `authenticationType` when a `userId` is actually present (otherwise `ClaimsIdentity.IsAuthenticated` is `true` for anonymous connections too — it depends solely on `authenticationType`, not claim count), and the .NET 10 rejection-path close frame has no `allowReconnect` field at all.

### Server-facing vs. client-facing protocols are different wire formats

- **Client ↔ Switchboard:** standard SignalR client protocol, unmodified. JSON frames delimited by `\x1e`; MessagePack frames length-prefixed.
- **App server ↔ Switchboard:** a single MessagePack, length-prefixed `ServerEnvelope` format (see [docs/docs/05-data-models.md](docs/docs/05-data-models.md)). The envelope's `payload` field carries the inner hub message as raw bytes — never base64, never re-encoded. `ServerEnvelope` fields are keyed by `[Key(n)]` position; append new fields with new keys, never reuse or reorder existing ones — this is a wire contract.

Rationale for WebSocket (not gRPC) on the server-facing side: [ADR-001](docs/docs/07-adr/ADR-001-transport-protocol.md).

### Registry and backplane: single-node now, Orleans later, no Redis ever

- **Phase 1:** `InMemoryConnectionRegistry` (`ConcurrentDictionary`), `NoOpBackplane`. `IConnectionRegistry` is async-from-day-one even though the in-memory impl is synchronous, specifically so Phase 3 is a substitution, not an interface change.
- **Phase 3:** `OrleansConnectionRegistry` delegates to grains (`IHubGrain`, `IGroupGrain`, `IUserGrain`, `IConnectionGrain`); the backplane uses **Orleans grain observers** (not Orleans Streams, not Redis Pub/Sub) — each node registers a local `HubObserverImpl` with the relevant hub grain, and broadcasts skip the origin node's observer to prevent self-echo. Local transport handles (`IClientTransport`) are *never* stored in grain state — they live in a node-local `ILocalTransportRegistry` singleton.

Why Orleans over Redis for both the registry and the backplane: [ADR-002](docs/docs/07-adr/ADR-002-connection-registry.md), [ADR-003](docs/docs/07-adr/ADR-003-backplane.md).

### Three independent token types — never reuse a signing key across them

| Token | Signing key | Audience | Lifetime |
|---|---|---|---|
| Client | `TokenSigningKey` | `switchboard-client` | ~60s |
| App server | `ServerSigningKey` | `switchboard-server` | ~24h |
| Management | `ManagementSigningKey` | `switchboard-management` | ~24h |

An app server token must never be able to drive the management API, and vice versa. Each has an independent `…Fallback` key for rotation. Rationale: [ADR-004](docs/docs/07-adr/ADR-004-token-authority.md).

### Solution layout (target, per the project plan)

```
Switchboard.sln
├── src/
│   ├── Keryhe.Switchboard.Core/        # interfaces/models, no ASP.NET dependency
│   ├── Keryhe.Switchboard.Protocol/    # ServerEnvelope MessagePack + client-facing frame parsing
│   ├── Keryhe.Switchboard.Server/      # main service host (Kestrel, DI wiring)
│   ├── Keryhe.Switchboard.Registry/    # IConnectionRegistry in-memory impl
│   ├── Keryhe.Switchboard.Orleans/     # grain interfaces + impls (Phase 3)
│   ├── Keryhe.Switchboard.Management/  # management REST API
│   └── Keryhe.Switchboard.Connector/   # app-server-side package (replaces AddAzureSignalR())
├── tests/
│   ├── Keryhe.Switchboard.UnitTests/
│   └── Keryhe.Switchboard.IntegrationTests/
└── samples/
    └── SampleChatApp/
        ├── SampleChatApp.Api/          # ASP.NET Core Web API using the Connector
        └── SampleChatApp.Angular/      # Angular SPA using @microsoft/signalr, unmodified
```

Full dependency graph and NuGet package list: [docs/docs/06-project-plan.md](docs/docs/06-project-plan.md).

### Current layout (Phase 0 spike, exists today)

```
spike/
├── Phase0.Spike.slnx
├── Phase0.Spike.Connector/   # carry-forward: MatcherPolicy, synthetic ConnectionContext, HubPipelineFactory
├── Phase0.Spike.Host/        # throwaway: test hubs, hand-rolled stub proxy target, dev JWT issuer
├── Phase0.Spike.Tests/       # 22 xunit tests, WorkstreamA/ (negotiate) + WorkstreamB/ (dispatch)
├── Phase0.Spike.JsClient/    # @microsoft/signalr redirect-check.mjs
└── findings/                 # API recon + the two corrections found during testing
```

## Documentation Map

Read in this order when picking up unfamiliar work:

| Doc | Read for |
|---|---|
| [docs/README.md](docs/README.md) | Orientation, doc index |
| [docs/docs/00-review-findings.md](docs/docs/00-review-findings.md) | Open questions and resolved design decisions — **check before assuming a design point is settled** |
| [docs/docs/01-overview.md](docs/docs/01-overview.md) | Problem statement, non-goals, glossary |
| [docs/docs/02-architecture.md](docs/docs/02-architecture.md) | Component topology, sequence diagrams |
| [docs/docs/03-protocol.md](docs/docs/03-protocol.md) | Exact wire format for both client-facing and server-facing protocols, plus the management REST API |
| [docs/docs/04-design.md](docs/docs/04-design.md) | Component interfaces and algorithms — the Connector sections (§8, §11) are the ones with real framework-internals risk |
| [docs/docs/05-data-models.md](docs/docs/05-data-models.md) | Concrete C# model/DTO shapes, including exact `[Key(n)]` envelope layout |
| [docs/docs/06-project-plan.md](docs/docs/06-project-plan.md) | Phased roadmap, solution structure, definition of done per phase |
| [docs/docs/07-adr/](docs/docs/07-adr/) | Why, not just what, for the five foundational decisions |
| [docs/docs/08-sample-app.md](docs/docs/08-sample-app.md) | Reference sample app used as the end-to-end integration target |

`00-review-findings.md` is a living log — when a design decision changes during implementation, that's the place resolutions get recorded, and it should be checked before treating any of the other docs as final on a contested point.

## Non-Goals (do not implement these)

- Multi-tenancy
- Message persistence/replay, including stateful reconnect (`.withStatefulReconnect()`) — clients that request it fall back to standard reconnect
- Azure Functions/serverless trigger mode
- Protocol translation to non-SignalR protocols (MQTT, AMQP, gRPC streams)
- Full Azure SignalR Service management API parity

Full list with rationale: [docs/docs/01-overview.md § Non-Goals](docs/docs/01-overview.md).
