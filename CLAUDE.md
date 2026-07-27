# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Status

**Phase 0 (Connector Mechanism Spike) is complete.** Both unproven Connector mechanisms — negotiate interception and inbound dispatch over a synthetic connection — are confirmed working against .NET 10, with no fallback needed. 22 automated tests pass plus a real out-of-process `@microsoft/signalr` client check. Full results: [docs/docs/00-review-findings.md § Phase 0 Spike Results](docs/docs/00-review-findings.md#phase-0-spike-results-2026-07-26), [docs/docs/09-phase0-findings/](docs/docs/09-phase0-findings/).

Two real design-doc defects were found and fixed by tests that exercised the design doc's own claims rather than by review — see [docs/docs/09-phase0-findings/inbound-dispatch-corrections.md](docs/docs/09-phase0-findings/inbound-dispatch-corrections.md) and the corresponding corrections already applied to [docs/docs/04-design.md §11](docs/docs/04-design.md#11-connector--inbound-dispatch-synthetic-client-connections).

**Phase 1 (Core Service MVP) is complete and the milestone is green.** `Switchboard.sln` holds all `src/` projects (`Core`, `Protocol`, `Registry`, `Server`, `Connector`), both `tests/` projects, and `samples/SampleChatApp/SampleChatApp.Api`. 32 unit tests + the out-of-process milestone integration test all pass, no skips. Full results: [docs/docs/00-review-findings.md § Phase 1 Results](docs/docs/00-review-findings.md).

The one bug that held up the milestone is worth knowing about, because the same mistake is easy to repeat: the service was **stripping the `\x1e` record separator** from `client_message` payloads. `JsonFrameProtocol.TryParseFrame` yields frames with the delimiter removed (correct for a frame reader), but the server-facing `payload` contract is raw hub-protocol bytes **including framing** — the Connector writes `payload` verbatim into the synthetic connection's pipe, where `IHubProtocol.TryParseMessage` needs the terminator to see a complete message. Without it the bytes sat unparsed forever: no dispatch, no exception, just a client-side timeout. **If you touch either side of that boundary, preserve the framing** — `ClientConnectionEndpoint.FrameForServer` is where it's re-applied, and the outbound direction is already framed because `IHubProtocol.WriteMessage` emits the delimiter itself.

**`spike/` has been retired.** Its verified framework findings live on at [docs/docs/09-phase0-findings/](docs/docs/09-phase0-findings/), and the `@microsoft/signalr` redirect-check script is parked at `samples/SampleChatApp/js-redirect-check/` for Phase 2 (it still targets the removed spike host and needs retargeting — see its README). The spike projects themselves are recoverable from git history at commit `b7456b2`.

> **Update this file after each phase completes.** When a phase (Phase 0, Phase 1, ...) is finished, revise "Project Status" to name the new current phase, and update any other section here that the completed phase changed (solution layout once scaffolded, architecture notes if implementation diverged from the design docs, etc.).

## What This Project Is

Switchboard is a self-hosted connection proxy and scale-out backplane for ASP.NET Core SignalR, written in .NET 10 C#. It solves the two classic SignalR scale-out problems without a cloud dependency:

1. **Connection offloading** — clients connect to Switchboard, not to app servers. App servers hold a small fixed pool of WebSocket connections to Switchboard (default 5 per hub) regardless of client count.
2. **Backplane** — because every client connects to Switchboard, it has a complete view of all connections and can fan out broadcasts/group/user messages across app servers with no sticky sessions.

Full rationale and comparison to Redis-backplane/Azure SignalR Service alternatives: [docs/docs/01-overview.md](docs/docs/01-overview.md).

## Commands

```bash
dotnet build Switchboard.sln
dotnet test tests/Keryhe.Switchboard.UnitTests/Keryhe.Switchboard.UnitTests.csproj
dotnet test tests/Keryhe.Switchboard.IntegrationTests/Keryhe.Switchboard.IntegrationTests.csproj
dotnet test tests/Keryhe.Switchboard.UnitTests/Keryhe.Switchboard.UnitTests.csproj --filter "FullyQualifiedName~SomeTestClass.SomeTestMethod"   # single test

# Generate a server or management access token (CLI mode of the Server host — see Cli/TokenCommand.cs):
dotnet run --project src/Keryhe.Switchboard.Server --no-build -- token generate --role appserver --server-id chat-api-1 --hubs chatHub --ttl 24h --key <ServerSigningKey>
```

Note: the milestone integration test spawns `Keryhe.Switchboard.Server.dll` and `SampleChatApp.Api.dll` as **real out-of-process Kestrel servers** (`tests/Keryhe.Switchboard.IntegrationTests/ProcessFixture.cs`), so build the solution first or it will fail on a missing assembly. Several unit tests likewise boot a real Kestrel host (`TestSupport/RealKestrelServerFixture.cs`) rather than `WebApplicationFactory`, because `TestServer`'s in-memory transport cannot do real WebSocket upgrades — a real `HubConnection` needs a real socket.

There is no linter configured in the repo yet — do not invent one.

## Architecture (Phase 1 implemented)

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

Both mechanisms were "designed against framework internals rather than exercised in code" — that's why Phase 0 existed before Phase 1 was planned. Both are now confirmed working in the real `src/Keryhe.Switchboard.Connector/` (promoted from the spike in Phase 1) with no fallback needed. See [docs/docs/00-review-findings.md](docs/docs/00-review-findings.md) for the history (a previous DI-override approach to negotiate interception turned out to be a silent no-op) and the Phase 0 results. Two subtleties the spike surfaced, already fixed in the spike code and reflected in the design doc: identity reconstruction must only set a non-null `authenticationType` when a `userId` is actually present (otherwise `ClaimsIdentity.IsAuthenticated` is `true` for anonymous connections too — it depends solely on `authenticationType`, not claim count), and the .NET 10 rejection-path close frame has no `allowReconnect` field at all.

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

### Solution layout (current — Phase 1)

```
Switchboard.sln
├── src/
│   ├── Keryhe.Switchboard.Core/        # interfaces/models, no ASP.NET dependency (Directory.Build.props enforces TreatWarningsAsErrors for src/)
│   ├── Keryhe.Switchboard.Protocol/    # ServerEnvelope MessagePack + client-facing \x1e frame parsing; also IServerConnection/IHubRegistry/ServerConnectionState (depend on ServerEnvelope, so they live here rather than Core)
│   ├── Keryhe.Switchboard.Server/      # main service host (Kestrel, DI wiring, negotiate/client/server-connection endpoints, JWT, CLI token command)
│   ├── Keryhe.Switchboard.Registry/    # InMemoryConnectionRegistry, InMemoryHubRegistry, PendingConnectionStore, LocalTransportRegistry, NoOpBackplane
│   └── Keryhe.Switchboard.Connector/   # app-server-side package (replaces AddAzureSignalR()) — negotiate interception, inbound dispatch, HubLifetimeManager, connection pool
├── tests/
│   ├── Keryhe.Switchboard.UnitTests/    # includes real-Kestrel-host e2e tests (TestSupport/RealKestrelServerFixture) since WebApplicationFactory's TestServer can't do real WebSockets
│   └── Keryhe.Switchboard.IntegrationTests/  # out-of-process milestone test (ProcessFixture spawns real `dotnet` processes)
└── samples/
    └── SampleChatApp/
        └── SampleChatApp.Api/          # ASP.NET Core Web API using the Connector (Angular is Phase 2 — not created yet)
```

`Keryhe.Switchboard.Orleans` and `Keryhe.Switchboard.Management` are Phase 3 / Phase 4 respectively — not created yet, per the project plan's "don't scaffold empty placeholder projects" guidance. Full dependency graph and NuGet package list: [docs/docs/06-project-plan.md](docs/docs/06-project-plan.md).

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
| [docs/docs/09-phase0-findings/](docs/docs/09-phase0-findings/) | Verified .NET 10 framework facts from the Phase 0 spike (API recon, the required synthetic-connection feature set, the two design-doc corrections) — preserved when `spike/` was retired |

`00-review-findings.md` is a living log — when a design decision changes during implementation, that's the place resolutions get recorded, and it should be checked before treating any of the other docs as final on a contested point.

## Non-Goals (do not implement these)

- Multi-tenancy
- Message persistence/replay, including stateful reconnect (`.withStatefulReconnect()`) — clients that request it fall back to standard reconnect
- Azure Functions/serverless trigger mode
- Protocol translation to non-SignalR protocols (MQTT, AMQP, gRPC streams)
- Full Azure SignalR Service management API parity

Full list with rationale: [docs/docs/01-overview.md § Non-Goals](docs/docs/01-overview.md).
