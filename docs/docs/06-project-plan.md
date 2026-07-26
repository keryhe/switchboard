# Implementation Roadmap

## Solution Structure

```
Switchboard.sln
├── src/
│   ├── Keryhe.Switchboard.Core/           # Interfaces, models, abstractions (no ASP.NET dependency)
│   ├── Keryhe.Switchboard.Protocol/       # Message types, envelope serialization, frame parsing
│   ├── Keryhe.Switchboard.Server/         # Main service host: Kestrel endpoints, DI wiring
│   ├── Keryhe.Switchboard.Registry/       # IConnectionRegistry implementations (in-memory)
│   ├── Keryhe.Switchboard.Orleans/        # Orleans grain interfaces + implementations (Phase 3)
│   │                                  #   OrleansConnectionRegistry, OrleansObserverBackplane,
│   │                                  #   HubGrain, GroupGrain, UserGrain, ConnectionGrain,
│   │                                  #   HubObserverImpl (IHubObserver)
│   ├── Keryhe.Switchboard.Management/     # Management REST API controllers
│   └── Keryhe.Switchboard.Connector/      # Client library app servers add to connect to this service
│                                      #   (replaces AddAzureSignalR() call)
├── tests/
│   ├── Keryhe.Switchboard.UnitTests/      # Pure unit tests (no I/O)
│   └── Keryhe.Switchboard.IntegrationTests/  # End-to-end: real client → service → real app server
└── samples/
    └── SampleChatApp/
        ├── SampleChatApp.Api/         # ASP.NET Core Web API — ChatHub, auth, uses Keryhe.Switchboard.Connector
        └── SampleChatApp.Angular/     # Angular SPA — chat UI, uses @microsoft/signalr
```

### Project Dependencies

```
Keryhe.Switchboard.Core          ← no external dependencies beyond BCL
Keryhe.Switchboard.Protocol      ← Core + System.Text.Json + MessagePack
Keryhe.Switchboard.Registry      ← Core
Keryhe.Switchboard.Orleans       ← Core + Microsoft.Orleans.Server
Keryhe.Switchboard.Management    ← Core + ASP.NET Core
Keryhe.Switchboard.Server        ← Core + Protocol + Registry + Orleans + Management + ASP.NET Core
Keryhe.Switchboard.Connector     ← Core + Protocol + Microsoft.AspNetCore.SignalR
```

### Key NuGet Packages

| Package | Version | Used In |
|---|---|---|
| `Microsoft.AspNetCore` | .NET 10 | Service, Management |
| `Microsoft.AspNetCore.SignalR` | .NET 10 | Connector (to implement `IHubLifetimeManager`) |
| `Microsoft.AspNetCore.SignalR.Protocols.MessagePack` | .NET 10 | Protocol |
| `System.IdentityModel.Tokens.Jwt` | 7.x | Service (JWT issue/validate) |
| `Microsoft.IdentityModel.Tokens` | 7.x | Service |
| `System.IO.Pipelines` | .NET 10 | Protocol (frame parsing) |
| `MessagePack` | 2.x | Protocol |
| `System.Threading.Channels` | .NET 10 | Service (write queues) |
| `Microsoft.Orleans.Server` | 10.x | Orleans (silo host, grain runtime, observers — no streaming needed) |
| `Microsoft.Orleans.Persistence.AdoNet` | 10.x | Orleans (grain state — SQL Server / PostgreSQL) |
| `Microsoft.Orleans.Clustering.AdoNet` | 10.x | Orleans (cluster membership table) |
| `Microsoft.Orleans.Persistence.Memory` | 10.x | Orleans (in-memory — dev / single-node) |
| `Microsoft.Extensions.ObjectPool` | .NET 10 | Service (buffer pooling) |
| `OpenTelemetry.Extensions.Hosting` | latest | Service (Phase 4) |
| `OpenTelemetry.Instrumentation.AspNetCore` | latest | Service (Phase 4) |

---

## Phase 0 — Connector Mechanism Spike ✅ Complete (2026-07-26)

Both mechanisms confirmed with no fallback needed. 22 automated tests + a real out-of-process `@microsoft/signalr` client check, all passing. Two design-doc corrections found and applied (identity-reconstruction `authenticationType` bug; rejection-frame shape). Full results: [00-review-findings.md § Phase 0 Spike Results](00-review-findings.md#phase-0-spike-results-2026-07-26), [spike/findings/](../../spike/findings/).

**Goal:** Prove the Connector's **two** unproven mechanisms — negotiate interception ([04-design.md §8](04-design.md)) and inbound dispatch over a synthetic connection ([04-design.md §11](04-design.md#11-connector--inbound-dispatch-synthetic-client-connections)) — before committing the Phase 1 design. Time-boxed, but **produces a reusable skeleton** that Phase 1 builds on directly — not disposable code (see *Output → Phase 1* below).

**Why first:** Together these two mechanisms *are* the Connector. Both were designed against framework internals rather than exercised in code, and the negotiate design has already been wrong once (the `NegotiateHandler` DI override that turned out to be a silent no-op — see [00-review-findings.md](00-review-findings.md)). Getting a wrong answer here after Phase 1 is underway is expensive.

**Deliverables — negotiate interception:**

- [x] **Detect the negotiate endpoint.** In a minimal `AddSignalR()` + `MapHub<T>("/testHub")` app, a `MatcherPolicy` implementing `IEndpointSelectorPolicy` can identify the `/testHub/negotiate` endpoint (confirm `NegotiateMetadata` — or whatever marker .NET 10 attaches — is accessible and present). **Biggest unknown.**
- [x] **Take over the endpoint.** In `ApplyAsync`, replace the matched candidate's endpoint (`CandidateSet.ReplaceEndpoint(...)` or equivalent) with a `RequestDelegate` that returns the redirect `{url, accessToken}`, and confirm the framework's built-in negotiate delegate never runs.
- [x] **Assert metadata is preserved.** With `[Authorize]` on the test hub class, an unauthenticated negotiate must still return 401 *after* the policy swaps the endpoint. This is the only surviving enforcement point for class-level hub authorization ([04-design.md §8](04-design.md)) — a green test here is the guard against silently dropping it.
- [x] **Prove the redirect end-to-end.** An unmodified `@microsoft/signalr` client and a .NET `HubConnection` receive the redirect, re-negotiate at the target (step 2), and open a transport against a stub target — purely via the registered policy, no SignalR fork or reflection into internals.
- [x] **Confirm ordering/isolation.** The policy fires ahead of the framework's negotiate handling regardless of policy order and does not disturb the transport endpoint or other mapped routes (MVC/minimal APIs).

**Deliverables — inbound dispatch:**

- [x] **Drive a hub method with no client.** Build `ConnectionDelegate` via `new ConnectionBuilder(sp).UseConnectionHandler<HubConnectionHandler<TestHub>>().Build()`, invoke it with a synthetic `ConnectionContext` backed by a `Pipe` pair, write a synthesized handshake plus a JSON `Invocation` frame into the input pipe, and confirm the hub method executes with correctly bound arguments.
- [x] **Confirm identity flows.** With `IConnectionUserFeature` set to a `ClaimsPrincipal` built with a non-null authentication type, `Context.User` is populated, `Context.UserIdentifier` matches the synthesized `NameIdentifier`, and a `[Authorize]`-decorated *hub method* is permitted; without it, denied.
- [x] **Confirm the return path split.** A `Completion` for the invocation arrives on the output pipe, while `Clients.All.SendAsync(...)` instead reaches the registered `IHubLifetimeManager` — verifying the two outbound paths in [§11](04-design.md#11-connector--inbound-dispatch-synthetic-client-connections) are distinct.
- [x] **Confirm lifecycle + rejection.** `OnConnectedAsync` runs on pipeline start and `OnDisconnectedAsync` on input completion; a hub whose `OnConnectedAsync` throws produces a close frame with `allowReconnect: false` rather than a hang.

**Success:** a standard, unmodified SignalR client is redirected via the policy alone, and a hub method executes from raw bytes with no client attached. **Fallback if negotiate interception fails** (likely at endpoint detection): skip `MapHub` for the connector and map an explicit higher-precedence `MapPost("/{hub}/negotiate", …)` ahead of it, sidestepping endpoint-selector policies — this would change [04-design.md §8](04-design.md). Inbound dispatch has no comparable fallback; if `HubConnectionHandler` cannot be driven over a synthetic context, the Connector's design needs rethinking before Phase 1 — which is precisely why it is proven here.

**Output → Phase 1.** Carried forward: the working `MatcherPolicy` skeleton (`AppliesToEndpoints` metadata detection, `ApplyAsync` endpoint replacement preserving metadata, redirect `RequestDelegate`), the synthetic `ConnectionContext` + `Pipe`-pair skeleton and its feature set, the verified facts (exact metadata type + replacement API), and the test clients (which seed the Phase 1 integration test). Discarded by Phase 1: the scaffolding only — stub redirect target, hardcoded tokens/URLs, throwaway host, test hub. If the negotiate spike fails, the fallback approach is carried forward instead and the policy code is dropped.

**Milestone check:** In a throwaway host, (a) an unmodified `@microsoft/signalr` client and a .NET `HubConnection` both negotiate against a `MapHub`-mapped route and get redirected to a stub target purely via the registered policy (or the fallback), with `[Authorize]` still enforced; and (b) a hub method runs to completion, with correct identity, driven only by bytes written into a synthetic connection's pipe — no SignalR fork, no reflection into internals.

---

## Phase 1 — Core Service (MVP)

**Goal:** A working single-node service where one ASP.NET Core app server connects and one client can send and receive hub method calls.

**Builds on Phase 0:** promotes the spike's negotiate-interception skeleton into `Keryhe.Switchboard.Connector` (swapping the stub target for the real proxy-forwarding call) and retires the spike scaffolding. Start from what Phase 0 proved rather than re-deriving it.

**Deliverables:**

- [ ] Solution and project scaffolding
- [ ] Promote the Phase 0 negotiate-interception skeleton into `Keryhe.Switchboard.Connector` (the working `MatcherPolicy`, or the fallback if the spike took it), replacing the stub redirect target with the real proxy-forwarding call; retire the Phase 0 scaffolding (throwaway host, stub target, hardcoded tokens) and fold its test clients into the integration test
- [ ] `Keryhe.Switchboard.Core` interfaces: `IConnectionRegistry`, `IHubRegistry`, `IServerConnection`, `IClientTransport`, `IMessageRouter`, `INegotiationService`
- [ ] `Keryhe.Switchboard.Protocol`: `ServerEnvelope` MessagePack serialization/deserialization (length-prefixed framing); client-facing JSON hub-protocol frame reader/writer using `\x1e` delimiter and `PipeReader`
- [ ] Negotiation endpoint: `POST /{hub}/negotiate` → JWT + redirect URL
- [ ] JWT issue and validation using `System.IdentityModel.Tokens.Jwt`
- [ ] WebSocket client transport: accept, handshake, read loop, write channel
- [ ] Server connection: app server WebSocket connection → handshake → register in Hub Registry
- [ ] `InMemoryConnectionRegistry` and `InMemoryHubRegistry`
- [ ] `DefaultMessageRouter`: `RouteClientMessage`, `RouteToConnection`, `Broadcast`
- [ ] `open_connection` / `close_connection` notifications to app server
- [ ] `Keryhe.Switchboard.Connector`: `IHubLifetimeManager` implementation — outbound only (`SendAllAsync`, `SendConnectionAsync`, group/user targeting → envelopes)
- [ ] `Keryhe.Switchboard.Connector` inbound dispatch ([04-design.md §11](04-design.md#11-connector--inbound-dispatch-synthetic-client-connections)), promoted from the Phase 0 skeleton: per-hub `ConnectionDelegate`; `SwitchboardClientConnectionContext` (synthetic `ConnectionContext` + `Pipe` pair + `IConnectionUserFeature` / `IConnectionIdFeature` / `IConnectionItemsFeature` / `IConnectionHeartbeatFeature`); `open_connection` → principal reconstruction + handshake synthesis + pipeline start; `client_message` → verbatim payload write; `close_connection` → teardown
- [ ] Connector outbound pipe reader: drop the synthetic handshake response and `PingMessage`, forward `Completion` / `StreamItem` / hub `Close` as `send_to_connection`
- [ ] Connector rejection path: hub `OnConnectedAsync` failure → `close_connection` with `error` back to the service
- [ ] `SwitchboardConnectorOptions`: `ServiceUrl`, `ServerAccessToken`, `ServerConnectionsPerHub`, `ReconnectDelay`
- [ ] CLI tool (`dotnet switchboard token generate --role appserver|management`) for generating server and management access tokens
- [ ] `PublicUrl` and `AllowedOrigins` in `SwitchboardOptions`; CORS middleware wired in service host
- [ ] Integration test: .NET `HubConnection` client negotiates through `SampleChatApp.Api`, connects to proxy, sends and receives hub messages end-to-end

**Milestone check:** `SampleChatApp.Api` connects to the proxy. A .NET SignalR client (using `Microsoft.AspNetCore.SignalR.Client`) negotiates through the API and successfully exchanges messages through the proxy. Integration test is green.

---

## Phase 2 — Full Transport & Protocol Support

**Goal:** Full SignalR transport and protocol compatibility. Existing apps need zero code changes beyond adding the connector package.

**Deliverables:**

- [ ] Server-Sent Events transport (read via POST, write via SSE stream)
- [ ] Long Polling transport (GET to receive, POST to send, DELETE to close)
- [ ] MessagePack hub protocol (frame reading/writing using length-prefix)
- [ ] Protocol negotiation: client chooses json or messagepack in handshake
- [ ] Group management: `add_to_group`, `remove_from_group`, group fan-out via `SendToGroupAsync`
- [ ] User targeting: `send_to_user`, user connection index in registry
- [ ] Streaming: `StreamInvocation` (client) and `StreamItem` / `Completion` sequence (server)
- [ ] `CancelInvocation` handling
- [ ] Hub-level `Ping` / `Close` message handling (distinct from transport-level keep-alive)
- [ ] `Send` and `SendCore` variants (with and without invocation ID)
- [ ] Excluded connection IDs in broadcast and group sends
- [ ] CORS policy applied and verified for browser clients (preflight on negotiate, `Origin` header on WebSocket upgrade)
- [ ] Pattern A (service-direct negotiate) — config + header handling, disabled by default: `EnableDirectNegotiate`, `TrustedIdentityHeader`, `TrustedClaimsHeader`, `TrustedProxyNetworks`; startup validation refuses to boot when enabled with an empty allowlist; identity headers stripped for non-allowlisted peers ([04-design.md §1](04-design.md)). Tests: allowlisted peer negotiates successfully; non-allowlisted peer asserting `X-Switchboard-UserId` is treated as anonymous, not trusted
- [ ] Angular `SampleChatApp.Angular` wired up to `SampleChatApp.Api`; full negotiate-through-API flow verified in browser
- [ ] Integration tests for each transport and protocol combination

**Milestone check:** `SampleChatApp.Angular` running in a browser negotiates through the API, opens a WebSocket to the proxy, and the full chat room flow (join, send, receive, leave) works. Standard .NET client also tested over all three transports.

---

## Phase 3 — Scale-Out & Resilience

**Goal:** Multiple service nodes and multiple app servers. No sticky sessions. Fault-tolerant reconnection. Orleans replaces both the distributed registry and the backplane — no Redis required.

**Deliverables:**

- [ ] `Keryhe.Switchboard.Orleans` project: define grain interfaces (`IHubGrain`, `IGroupGrain`, `IUserGrain`, `IConnectionGrain`) and observer interface (`IHubObserver`) with `[GenerateSerializer]` attributes
- [ ] Grain implementations: state management, connection registration, observer fan-out (skipping `originNodeId` to prevent self-echo)
- [ ] `HubObserverImpl`: plain class (not a grain) implementing `IHubObserver`; registered per silo; uses `ILocalTransportRegistry` to deliver to local transports
- [ ] `ILocalTransportRegistry`: singleton in-process registry mapping `connectionId → IClientTransport` (node-local, never persisted to grains)
- [ ] `OrleansConnectionRegistry`: implement `IConnectionRegistry` by delegating to grains via `IGrainFactory`
- [ ] `OrleansObserverBackplane`: implement `IBackplane` using `IHubGrain` observer fan-out (no stream provider required)
- [ ] Node ID generation: GUID per silo instance, passed as `originNodeId` in all grain broadcast calls
- [ ] Silo co-hosting: wire Orleans silo into `IHostBuilder` alongside Kestrel; configure in-memory providers for dev, ADO.NET providers for production
- [ ] ADO.NET schema: SQL scripts for Orleans cluster membership table and grain state tables (SQL Server + PostgreSQL variants)
- [ ] Multiple app server connections per hub (Pool): selector chooses least-loaded connection
- [ ] Server connection pool management: watch for disconnected servers, remove from hub grain
- [ ] Client reconnect support: on server connection loss, send `Close{allowReconnect:true}` to affected clients
- [ ] Load balancer integration: `/healthz` returns 200 only when silo is active and at least one server connection exists per registered hub
- [ ] Integration tests: two service nodes (two silos), two app servers, client connected to Node A receives broadcast from app server connected to Node B

**Milestone check:** Rolling restart of a service node does not drop client connections that reconnect within the reconnect window. Broadcasting works across nodes. No Redis anywhere in the stack.

---

## Phase 4 — Management & Observability

**Goal:** Operations team can inspect service state and send messages without a SignalR client. Full observability via standard tooling.

**Deliverables:**

- [ ] `Keryhe.Switchboard.Management` REST API (all endpoints from [Protocol Specification](03-protocol.md) Part 3)
- [ ] Management API auth: management access token signed with `ManagementSigningKey` (third independent secret), `role: management` claim required, `aud` = `ManagementAudience`; server access tokens explicitly rejected ([03-protocol.md Part 3](03-protocol.md#part-3-management-rest-api))
- [ ] OpenTelemetry metrics:
  - `signalr.client_connections.active` (gauge, by hub)
  - `signalr.server_connections.active` (gauge, by hub)
  - `signalr.messages.routed` (counter, by direction and hub)
  - `signalr.broadcast.fan_out_size` (histogram)
  - `signalr.message.latency` (histogram, client→server round trip)
- [ ] OpenTelemetry tracing: spans for negotiate, client connect, message route
- [ ] Structured logging (Microsoft.Extensions.Logging): connection lifecycle events, routing errors, server connection health changes
- [ ] `/healthz` endpoint: public, unauthenticated liveness/readiness (200/503, no topology detail); detailed per-hub connection status behind the authenticated management API (`GET /api/v1/health`)
- [ ] `/metrics` endpoint (Prometheus format via `OpenTelemetry.Exporter.Prometheus.AspNetCore`)
- [ ] Admin UI docs (optional): Swagger/OpenAPI spec for management API
- [ ] Operations guide: the three token types and their independent signing keys, generation via the CLI, rotation procedure using the `…Fallback` keys, and secret storage guidance ([ADR-004](07-adr/ADR-004-token-authority.md))

**Milestone check:** Grafana dashboard showing active connections, message throughput, and fan-out size can be built from `/metrics`. An on-call engineer can broadcast a maintenance notice via `curl`.

---

## Phase 5 — Compatibility Testing & Benchmarking

**Goal:** Validate that existing real-world apps work without modification and characterize performance limits.

**Deliverables:**

- [ ] Compatibility test matrix: each official SignalR client SDK × each transport × each protocol
  - .NET 8 client, .NET 10 client
  - JavaScript / TypeScript client (npm `@microsoft/signalr`) — primary target, used by `SampleChatApp.Angular`
  - Java client
- [ ] End-to-end test of `SampleChatApp.Angular` + `SampleChatApp.Api` against the proxy: negotiate flow, group messaging, server-initiated push, reconnect
- [ ] Verify `AddSwitchboardConnector()` is a drop-in for `AddAzureSignalR()` (same `IHubLifetimeManager` contract)
- [ ] Benchmark suite using `BenchmarkDotNet`:
  - Negotiate throughput (connections/sec)
  - Message routing latency (P50, P95, P99)
  - Broadcast fan-out throughput (messages/sec × connection count)
  - Memory per connection
- [ ] Load test: 10,000 simulated clients using `Microsoft.AspNetCore.SignalR.Client` in headless mode
- [ ] Document observed limits and recommended Kestrel/OS tuning (file descriptor limits, thread pool, etc.)

---

## Risk Register

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| SignalR client protocol has undocumented edge cases | Medium | High | Run full test matrix against real client libraries; compare with azure-signalr open-source SDK |
| MessagePack framing differences between clients | Medium | Medium | Use official `MessagePack-CSharp` library; cross-test with JS client |
| Orleans grain call latency adds routing overhead | Medium | Medium | Benchmark grain lookups; use `ILocalTransportRegistry` to avoid grain calls on the hot path for local clients |
| Orleans silo startup time delays service readiness | Low | Medium | Emit `/healthz` as not-ready until silo is fully started; configure load balancer readiness probe accordingly |
| Orleans cluster split-brain under network partition | Low | High | Configure Orleans partition tolerance settings; document that cross-node messages are degraded (not lost locally) during partition |
| Server connection pool exhaustion under burst load | Medium | Medium | Expose pool size as config; emit metric when pool is saturated |
| Memory growth on slow clients | Medium | Medium | Bounded write channels with configurable drop policy; integration test with throttled client |
| JWT secret rotation | Low | Medium | Support multiple valid secrets simultaneously (key rotation window); document key rollover procedure |
| Orleans serialization errors for new message types | Medium | Low | Enforce `[GenerateSerializer]` on all Orleans-serialized types via analyzer; integration test observer round-trip on every `BackplaneMessage` variant |
| Observer registration lost after silo restart | Low | Medium | Re-register `HubObserverImpl` on `IHostedService.StartAsync`; grain cleans up stale references when observer calls fail |
| Negotiate forwarding fails if proxy is down | Medium | High | Connector returns 503 with `Retry-After` header; app server `/healthz` reflects degraded state; configure load balancer to stop routing to degraded app servers |
| Hub code calls `Context.GetHttpContext()` — always `null` on synthetic connections | Medium | Medium | Documented incompatibility ([04-design.md §11](04-design.md#11-connector--inbound-dispatch-synthetic-client-connections)); pass any required request data as claims via `open_connection`; add to the Phase 5 compatibility matrix |
| Custom `IUserIdProvider` diverges from the service's user index, silently breaking `Clients.User(...)` | Medium | Medium | Connector synthesizes `NameIdentifier` from the envelope `userId` so the default provider agrees; document that a custom provider must be mirrored in the app server's forwarded negotiate; integration test asserts round-trip |
| Class-level `[Authorize]` silently dropped when the MatcherPolicy replaces the negotiate endpoint | Low | High | Replacement endpoint is built from the original's `Metadata`; asserted directly in the Phase 0 spike (401 on unauthenticated negotiate against an `[Authorize]` hub) |

---

## Definition of Done (Per Phase)

A phase is complete when:
1. All checklist items are implemented
2. All existing passing tests still pass
3. Phase-specific integration tests are added and passing
4. The milestone check scenario works end-to-end
5. No unresolved TODO/FIXME comments in new code
