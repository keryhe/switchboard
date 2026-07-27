# Phase 1 — Core Service (MVP): Implementation Plan

**Source of truth:** [06-project-plan.md § Phase 1](../docs/docs/06-project-plan.md), [04-design.md §§1–5, 8, 11](../docs/docs/04-design.md), [03-protocol.md Parts 1–2](../docs/docs/03-protocol.md), [05-data-models.md](../docs/docs/05-data-models.md), [00-review-findings.md](../docs/docs/00-review-findings.md).

**Goal.** One ASP.NET Core app server connects to the service; one client negotiates through that app server, connects to the service, and exchanges hub method calls in both directions. Single node, WebSocket only, JSON hub protocol only.

**Milestone check.** `SampleChatApp.Api` connects to the proxy. A .NET `HubConnection` negotiates through the API, connects to the proxy, and successfully exchanges messages. Integration test green.

**This is the largest phase in the roadmap** — 19 deliverables, 7 new projects, plus a sample app. It is planned below as six vertical slices, each ending in something runnable and independently testable, rather than as horizontal layers where nothing works until the end.

---

## 1. Preconditions — what Phase 0 already settled

Phase 0 is complete ([results](../docs/docs/00-review-findings.md#phase-0-spike-results-2026-07-26)). Do **not** re-derive any of this:

| Established | Consequence for Phase 1 |
|---|---|
| `MatcherPolicy` + `IEndpointSelectorPolicy` + `CandidateSet.ReplaceEndpoint` negotiate interception works, metadata preserved, `[Authorize]` enforced | Promote `SwitchboardNegotiateMatcherPolicy` as-is; only the redirect body changes (stub → real proxy call) |
| `HubConnectionHandler<THub>` drives correctly over a synthetic `ConnectionContext` + `Pipe` pair | Promote the whole `Dispatch/` folder; it is proven, not speculative |
| Required feature set is **six**, not four (`IConnectionLifetimeFeature` + `IConnectionCompleteFeature` added) | Already reflected in the spike code and [04-design.md §11](../docs/docs/04-design.md#11-connector--inbound-dispatch-synthetic-client-connections) |
| `authenticationType` must be conditional on `userId` or anonymous connections authenticate | Fix already applied in `IdentityReconstruction`; **do not regress it** — there is a test for this, carry it forward |
| .NET 10 rejection close frame is `{"type":7,"error":"..."}` with **no** `allowReconnect` key | Connector's outbound reader must not assume the field exists |
| An unmodified `@microsoft/signalr` client and a .NET `HubConnection` both complete the redirect flow | Both are the ground-truth clients for Phase 1's integration test |

**New framework facts verified while writing this plan** (reflection against shared framework 10.0.5):

- `Microsoft.AspNetCore.Http.Connections.NegotiateProtocol` **is public** (it lives in `Http.Connections.Common.dll`, not `Http.Connections.dll`), exposing `WriteResponse(NegotiationResponse, IBufferWriter<byte>)` and `ParseResponse(ReadOnlySpan<byte>)`.
- `NegotiationResponse` carries exactly the fields both negotiate responses need: `Url`, `AccessToken`, `ConnectionId`, `ConnectionToken`, `Version` (serializes as `negotiateVersion`), `AvailableTransports`, `Error`, `UseStatefulReconnect`.
- **But its output is not identical for both shapes.** Verified actual output:
  - Step-2 connect response → `{"negotiateVersion":1,"connectionId":"…","connectionToken":"…","availableTransports":[…]}` — **exactly** the shape [03-protocol.md §1.1](../docs/docs/03-protocol.md#11-negotiation) specifies. Use it.
  - Redirect response → `{"negotiateVersion":0,"url":"…","accessToken":"…","availableTransports":[]}` — carries two extra fields the spec says the redirect must **not** have ("carries **only** `url` and `accessToken`"). Phase 0 proved the hand-written `{url, accessToken}` body works with both real clients.
- `Microsoft.AspNetCore.SignalR.HubMetadata` is public with a `HubType` property, and is attached to every `MapHub<T>()` endpoint (seen in the Phase 0 endpoint dump). This is how the Connector can discover hub name **and** hub type at startup — see Decision 2.

**Recommendation:** service step-2 response uses `NegotiateProtocol.WriteResponse` (free wire compatibility); the Connector's redirect body stays hand-written to match the spec exactly. This makes the custom `NegotiateResponse` record in [05-data-models.md](../docs/docs/05-data-models.md) redundant — see §7 doc updates.

---

## 2. Decisions (settled — accepted 2026-07-26)

Five genuine ambiguities in the current design docs, each with a recommendation below. All five recommendations are **accepted** — the slices in §4 already implement them as written, so no further sign-off is needed before coding. This section is the record of what was decided and why; revisit it only if implementation surfaces a reason one of these doesn't hold up.

### D1 — How does the service's `POST /{hub}/negotiate` tell step 1 from step 2?

Both steps hit the **same URL** on the service with different callers, token types, and response shapes. [04-design.md §1](../docs/docs/04-design.md) defines `IssueRedirectAsync` and `NegotiateAsync` separately but never says how the endpoint dispatches between them.

**Recommendation:** dispatch on the **validated token type**, never on a query parameter or header the caller controls:

| Presented token | Validated against | Meaning | Response |
|---|---|---|---|
| `role: appserver`, `aud: switchboard-server` | `ServerSigningKey` | Step 1 — app server forwarding a negotiate | Redirect `{url, accessToken}` |
| `aud: switchboard-client` | `TokenSigningKey` | Step 2 — client re-negotiating | `{connectionId, connectionToken, negotiateVersion, availableTransports}` |
| Neither / invalid | — | — | `401` |

Getting this wrong in either direction is a security bug (a client token minting redirects, or an app server token claiming a client connection), so it deserves a dedicated test per row including the negative case.

### D2 — How does the Connector discover which hubs to open server connections for?

It needs, at startup, both the hub **name** (for `wss://service/server/{hubName}`) and the hub **Type** (to build `HubConnectionHandler<THub>` pipelines).

**Recommendation:** enumerate `EndpointDataSource` in `IHostedService.StartAsync`, reading the route pattern for the name and `HubMetadata.HubType` for the type. This matches the "zero app-server code changes" principle in [02-architecture.md](../docs/docs/02-architecture.md) — the app author writes `MapHub<ChatHub>("/chatHub")` and nothing else.

Two constraints this imposes, both fine but worth knowing up front:
- `EndpointDataSource` is not populated at DI-registration time; discovery **must** happen in `StartAsync`, not inside `AddSwitchboardConnector()`.
- Building a pipeline from a runtime `Type` needs one `MakeGenericMethod` call per hub. That is startup-only reflection, not hot path.

*Alternative if that proves awkward:* explicit `AddSwitchboardConnector().AddHub<ChatHub>()` registration. Cheaper to implement, but pushes work onto the app author — prefer the automatic path and fall back only if discovery misbehaves.

### D3 — What happens to group/user envelopes in Phase 1?

`HubLifetimeManager<THub>` is an abstract class with **13 abstract methods** (verified) — the Connector must implement every one in Phase 1, including groups and users, whose *routing* is a Phase 2 deliverable.

**Recommendation:** the Connector emits the correct envelope for every method (so the wire format is exercised now and Phase 2 becomes a service-side-only change), and the service **logs a warning** for envelope types it does not yet route. Explicitly **not** a silent drop — a message that goes nowhere must be visible in logs. Document the Phase 1 gap in the Connector's XML docs so an app author calling `Clients.Group(...)` against a Phase 1 service gets an answer from the logs immediately.

> **Logging only in Phase 1 — no metric.** OpenTelemetry instrumentation is a Phase 4 deliverable ([06-project-plan.md](../docs/docs/06-project-plan.md)); there is no metrics infrastructure to hang a counter on yet. A companion counter for unrouted envelopes is queued for Phase 4 — see §7.

### D4 — Pending-connection state between step-2 negotiate and the transport upgrade

Step 2 mints a `connectionToken` and returns it; the client then opens the transport with `id={connectionToken}`. Something must remember that token in between. This store is not described in the design docs.

**Recommendation:** a TTL'd pending-connection map keyed by `connectionToken`, with the TTL matched to `ClientTokenExpiry` (60s default) and a background reaper. A transport upgrade presenting an unknown or expired token gets `404`/`401`, never a new connection. Phase 1 observability for this store is logging only, for the same reason as D3; a `signalr.pending_connections.active` gauge is queued for Phase 4 — see §7.

### D5 — Client arrives before any app server has connected

[04-design.md §2](../docs/docs/04-design.md) accept phase step 5 says "Finds (**or waits for**) an available server connection."

**Recommendation for Phase 1: fail fast, do not wait.** [03-protocol.md §1.1](../docs/docs/03-protocol.md#11-negotiation) already specifies `503 Service Unavailable — no app servers registered for this hub`; return that at **step-2 negotiate** (earliest point we know) rather than accepting a transport we cannot service. Waiting/queueing is a Phase 3 resilience concern, not an MVP one.

### D6 (no decision needed — record the reasoning)

**`open_connection` is emitted only after the hub-protocol handshake completes**, because it must carry `hubProtocol` ([03-protocol.md §2.3](../docs/docs/03-protocol.md#23-message-envelope-format)). There is deliberately no ack ([03-protocol.md §2.3](../docs/docs/03-protocol.md#23-message-envelope-format)). So a client could in principle invoke a hub method before the app server knows the connection exists.

**Why no buffering is needed:** `open_connection` and every subsequent `client_message` for that connection are written to the **same** multiplexed server WebSocket, in order. TCP and the single writer preserve that order, so the app server always processes `open_connection` first. This holds only as long as a connection's envelopes are never written to two different server connections — which [03-protocol.md §2.4](../docs/docs/03-protocol.md#24-connection-multiplexing) already guarantees ("all messages for that client flow over that same server connection for the lifetime of the client connection"). **Add a test that asserts this ordering**, since the correctness argument depends on it.

---

## 3. Target layout

```
Switchboard.sln
├── src/
│   ├── Keryhe.Switchboard.Core/          # interfaces + models, no ASP.NET dependency
│   ├── Keryhe.Switchboard.Protocol/      # ServerEnvelope MessagePack + JSON \x1e framing
│   ├── Keryhe.Switchboard.Registry/      # InMemoryConnectionRegistry, InMemoryHubRegistry
│   ├── Keryhe.Switchboard.Server/        # service host: Kestrel, negotiate, transports, router, CLI
│   └── Keryhe.Switchboard.Connector/     # app-server package (promoted from spike)
├── tests/
│   ├── Keryhe.Switchboard.UnitTests/
│   └── Keryhe.Switchboard.IntegrationTests/
├── samples/
│   └── SampleChatApp/
│       └── SampleChatApp.Api/            # ChatHub + auth + Connector (Angular is Phase 2)
└── spike/                                # DELETED at the end of Phase 1 (see §6)
```

`Keryhe.Switchboard.Management` and `Keryhe.Switchboard.Orleans` are **not** created in Phase 1 — they are Phase 4 and Phase 3 respectively. Creating empty placeholder projects now is worse than nothing.

### Promotion map (Phase 0 → Phase 1)

| Spike file | Destination | Change on promotion |
|---|---|---|
| `Negotiate/SwitchboardNegotiateMatcherPolicy.cs` | `Connector/Negotiate/` | None to the policy itself |
| `Negotiate/INegotiateRedirectHandler.cs` | `Connector/Negotiate/` | Implementation swaps stub → real `POST {ServiceUrl}/{hub}/negotiate` via `IHttpClientFactory` |
| `Dispatch/SwitchboardClientConnectionContext.cs` | `Connector/Dispatch/` | None (six features already correct) |
| `Dispatch/HubPipelineFactory.cs` | `Connector/Dispatch/` | Add non-generic `GetOrCreate(Type)` overload for D2 discovery |
| `Dispatch/HandshakeWriter.cs`, `IdentityReconstruction.cs`, `DuplexPipe.cs` | `Connector/Dispatch/` | None — **keep the `authenticationType` fix** |
| `Phase0.Spike.Tests/WorkstreamA/*`, `WorkstreamB/*` | `tests/Keryhe.Switchboard.UnitTests/` | Retarget namespaces; `HostProcessFixture` pattern reused for the integration test |
| `Phase0.Spike.Host/*` | — | **Discarded.** Its `TestHub`/`SecureHub` inform `SampleChatApp.Api`'s `ChatHub`; the stub target is replaced by the real service |
| `Phase0.Spike.JsClient/` | Hold until Phase 2 | The JS client belongs with the Angular deliverable; keep the script until then |

---

## 4. Slices

Each slice ends **runnable and testable on its own**. Do not start a slice before its predecessor's gate is green.

### Slice 0 — Scaffolding and contracts

- Create the solution, five `src` projects, two `tests` projects, with the dependency graph from [06-project-plan.md](../docs/docs/06-project-plan.md) (`Core` has no ASP.NET reference — enforce it by *not* adding the `FrameworkReference`, so a violation is a build error).
- `Keryhe.Switchboard.Core`: `IConnectionRegistry`, `IHubRegistry`, `IServerConnection`, `IClientTransport`, `IMessageRouter`, `INegotiationService`, plus `ClientConnectionState`, `ServerConnectionState`, `HubDescriptor`, `SwitchboardOptions` from [05-data-models.md](../docs/docs/05-data-models.md).
- `IConnectionRegistry` is **async from day one** even though the in-memory implementation is synchronous — [ADR-002](../docs/docs/07-adr/ADR-002-connection-registry.md) requires this so Phase 3 is a substitution, not an interface change.
- Add a root `Directory.Build.props` setting `TreatWarningsAsErrors` for `src` (the spike accumulated six nullability warnings; don't let that pattern start here).

**Gate:** solution builds; `Core` provably has no ASP.NET dependency.

### Slice 1 — Protocol primitives

- `ServerEnvelope` as `[MessagePackObject]` with the exact `[Key(n)]` layout in [05-data-models.md](../docs/docs/05-data-models.md), and length-prefixed read/write. **`[Key(n)]` order is a wire contract** — add a test that pins the serialized bytes of a known envelope so a future reorder fails loudly.
- Client-facing JSON hub-protocol frame reader/writer: `\x1e` delimiter over `PipeReader`, handling partial frames, multiple frames in one read, and frames spanning segments. The spike's `JsonFrameIO` is a starting point but is test-grade — this one is production code and needs the segment-spanning cases the spike never hit.
- `payload` stays **raw bytes**, never transcoded, never base64 ([03-protocol.md Part 2](../docs/docs/03-protocol.md#part-2-server-facing-protocol-app-server--service)).

**Gate:** round-trip unit tests, including a byte-pinned envelope test and adversarial framing (split mid-frame, several frames per buffer, empty frame).

### Slice 2 — Negotiate + JWT + registries (service side, HTTP only)

- `JwtTokenService`: issue/validate all three token types with independent keys, `…Fallback` support ([ADR-004](../docs/docs/07-adr/ADR-004-token-authority.md)). Fail startup on a missing required key.
- `DefaultNegotiationService`: `IssueRedirectAsync` (step 1) and `NegotiateAsync` (step 2), dispatched per **D1**.
- Step-2 response via `NegotiateProtocol.WriteResponse`; never set `UseStatefulReconnect` (non-goal — [ADR-005](../docs/docs/07-adr/ADR-005-protocol-compatibility.md)).
- `InMemoryConnectionRegistry`, `InMemoryHubRegistry`, pending-connection store per **D4**.
- `PublicUrl` (required, fail-fast if unset) + `AllowedOrigins` + CORS middleware.
- 503 when no server connection exists for the hub per **D5**.

**Gate:** `POST /{hub}/negotiate` returns the right shape for each token type over real HTTP; wrong/missing token → 401; no app servers → 503. No transport involved yet.

### Slice 3 — Server connection (app server ↔ service)

- `wss://service/server/{hubName}` endpoint: validate server token (`role: appserver`, `hubs` claim covers the requested hub — reject otherwise), accept WebSocket, run the `Handshake` / `HandshakeAck` / `HandshakeError` exchange from [03-protocol.md §2.2](../docs/docs/03-protocol.md#22-server-handshake).
- Register in `InMemoryHubRegistry`; deregister on close.
- Envelope read loop + a single-writer write path (the ordering guarantee in **D6** depends on one writer per server connection).
- Ping/pong keep-alive ([04-design.md §3](../docs/docs/04-design.md)).

**Gate:** a test double acting as an app server connects, handshakes, appears in the hub registry, survives ping/pong, and disappears cleanly on disconnect. Rejected: bad token, hub not in `hubs` claim, version mismatch.

### Slice 4 — Client transport + router (completes the service)

- WebSocket accept at `GET /{hub}?id={connectionToken}&access_token={jwt}`: validate token, resolve `connectionToken` via the pending store, register a **pending** connection with `HubProtocol = null`, register the transport in `ILocalTransportRegistry`, assign a server connection.
- Handshake phase: send/receive the hub-protocol handshake, then `SetProtocolAsync`, then emit `open_connection` **with** `hubProtocol`.
- Per-connection bounded write channel — `WriteChannelFullMode = DropWrite` ([00-review-findings.md](../docs/docs/00-review-findings.md) settled this; a slow client must never stall fan-out).
- `DefaultMessageRouter`: `RouteClientMessageAsync`, `RouteToConnectionAsync`, `BroadcastAsync`. Group/user per **D3**.
- Disconnect: `close_connection` to the app server, unregister, complete the write channel.

**Gate:** a real .NET `HubConnection` connects to the service against the Slice 3 test double; the double observes `open_connection` then `client_message` **in that order** (the D6 test); a `send_to_connection` from the double reaches the client; a `broadcast` reaches two connected clients.

### Slice 5 — Connector (app-server side)

- `AddSwitchboardConnector()`: registers the promoted `MatcherPolicy`, `SwitchboardConnectorOptions`, the named `"switchboard-negotiate"` `HttpClient`, and the hosted service.
- Real redirect handler: forwards negotiate to the service with `Authorization: Bearer <server token>` + `X-Switchboard-UserId` / `X-Switchboard-Claims`, returns `{url, accessToken}` verbatim to the client. On proxy failure → `503` with `Retry-After` (risk-register mitigation).
- Hosted service: hub discovery per **D2**, then `ServerConnectionsPerHub` WebSockets per hub with exponential backoff (base `ReconnectDelay`, cap 60s).
- `SwitchboardHubLifetimeManager<THub>` — outbound only, all 13 methods, group/user per **D3**.
- Inbound dispatch: promoted `Dispatch/` code driving `open_connection` → principal + handshake synthesis + pipeline start; `client_message` → verbatim pipe write; `close_connection` → teardown.
- Outbound pipe reader: **drop** the synthetic handshake response and `PingMessage`; **forward** `Completion` / `StreamItem` / hub `Close` as `send_to_connection`. Must not assume `allowReconnect` exists on a close frame (Phase 0 finding).
- Rejection path: `OnConnectedAsync` throws → `close_connection` with `error`.

**Gate:** app server + service both running; a hub method invoked by a real client executes on the app server and its return value reaches the client.

### Slice 6 — Sample app, CLI, and the end-to-end test

- `SampleChatApp.Api`: `ChatHub` (with class-level `[Authorize]`, so the Phase 0 metadata-preservation guarantee is exercised for real), JWT auth, `AuthController` login, `AddSwitchboardConnector()` — per [08-sample-app.md](../docs/docs/08-sample-app.md).
- CLI: `dotnet switchboard token generate --role appserver|management …`.
- **Integration test (the milestone):** start the service and `SampleChatApp.Api` as real processes (reuse the proven `HostProcessFixture` pattern), authenticate, negotiate through the API, connect through the proxy, invoke a hub method, receive a server-initiated broadcast, disconnect cleanly.

**Gate:** the milestone check.

---

## 5. Testing strategy

Phase 0's failures were valuable precisely because the assertions were specific. Carry that forward:

- **Assert absence, not just presence.** The step-1 redirect must *not* contain `connectionId`; a Phase 1 service must *not* silently accept a group send.
- **Bound every async wait.** A deadlock must be a red test, not a hung CI job. Every pipeline/socket await gets an explicit timeout, as in the spike's B-workstream tests.
- **Real clients are ground truth.** `Microsoft.AspNetCore.SignalR.Client` for Phase 1; the `@microsoft/signalr` script returns in Phase 2 with Angular.
- **Pin the wire contracts.** Byte-level tests for the `ServerEnvelope` `[Key(n)]` layout and for the two negotiate response shapes. These are the things that break silently and expensively later.
- **Test the security dispatch matrix explicitly** (D1), including every negative row.

---

## 6. Deliverable ↔ slice mapping

Every checkbox in [06-project-plan.md § Phase 1](../docs/docs/06-project-plan.md):

| Deliverable | Slice |
|---|---|
| Solution and project scaffolding | 0 |
| Promote Phase 0 skeleton into `Connector`; retire spike scaffolding | 5 (code), 6 (spike deletion) |
| `Core` interfaces | 0 |
| `Protocol`: `ServerEnvelope` MessagePack + JSON `\x1e` framing | 1 |
| Negotiation endpoint | 2 |
| JWT issue/validate | 2 |
| WebSocket client transport | 4 |
| Server connection + handshake + hub registry | 3 |
| `InMemoryConnectionRegistry` / `InMemoryHubRegistry` | 2 |
| `DefaultMessageRouter` | 4 |
| `open_connection` / `close_connection` notifications | 4 |
| Connector `IHubLifetimeManager` (outbound) | 5 |
| Connector inbound dispatch | 5 |
| Connector outbound pipe reader | 5 |
| Connector rejection path | 5 |
| `SwitchboardConnectorOptions` | 5 |
| CLI token tool | 6 |
| `PublicUrl` / `AllowedOrigins` + CORS | 2 |
| End-to-end integration test | 6 |

**Retiring the spike.** Delete `spike/` in Slice 6, once its tests live under `tests/` and the milestone is green — not before. Keep `spike/findings/` by moving it to `docs/docs/09-phase0-findings/`, since those are verified framework facts with continuing reference value, not scaffolding.

---

## 7. Documentation updates due at the end of Phase 1

- **[05-data-models.md](../docs/docs/05-data-models.md)** — replace the custom `NegotiateResponse` record with the framework's `NegotiationResponse` + `NegotiateProtocol.WriteResponse`, noting that the redirect body stays hand-written (verified reason in §1 above). Keep `RedirectResponse`.
- **[04-design.md §1](../docs/docs/04-design.md)** — document the D1 dispatch rule; it is currently unspecified.
- **[04-design.md §2](../docs/docs/04-design.md)** — document the D4 pending-connection store, and narrow "finds (or waits for)" to Phase 1's fail-fast behavior (D5).
- **[03-protocol.md §2.4](../docs/docs/03-protocol.md#24-connection-multiplexing)** — add the D6 ordering guarantee explicitly as a *requirement* (single writer per server connection), since correctness depends on it.
- **[06-project-plan.md](../docs/docs/06-project-plan.md)** — tick Phase 1; note anything Phase 2 inherits.
- **[06-project-plan.md § Phase 4](../docs/docs/06-project-plan.md)** — add the two metrics Phase 1 deliberately deferred for want of instrumentation (D3, D4): a counter for envelope types received but not routed, and `signalr.pending_connections.active` (gauge) for the D4 pending-connection store. Both are logging-only in Phase 1; without this entry the intent is lost when Phase 4 builds the metric list.
- **[00-review-findings.md](../docs/docs/00-review-findings.md)** — a Phase 1 results entry, same format as Phase 0's.
- **[CLAUDE.md](../CLAUDE.md)** — per the standing instruction, update Project Status, the Commands section (real `Switchboard.sln` commands replace the spike ones), and the layout section.

---

## 8. Risks

| Risk | Mitigation |
|---|---|
| Slices 3–5 each pass in isolation but the seams leak (envelope written by one side isn't what the other parses) | Byte-pinned protocol tests in Slice 1; every slice gate uses a *real* counterpart (real client, real WebSocket) rather than a mock of the thing being integrated |
| D6's ordering argument is wrong under load (e.g. two writers on one server connection) | Enforce single-writer at the type level in Slice 3; explicit ordering test in Slice 4's gate |
| Phase 1 accidentally implements Phase 2 scope (groups, users, SSE, MessagePack) because the interfaces are already there | `IMessageRouter` group/user methods and the Connector's group/user paths follow D3 exactly — emit + log, don't route. Scope creep here is the most likely cause of Phase 1 overrunning |
| The promoted Connector code drifts from the spike's proven behavior during the move | Promote the spike's tests **first**, watch them pass against the moved code, and only then change the redirect handler |
| Two processes + one client makes the integration test flaky | Reuse the `HostProcessFixture` readiness-polling pattern proven in Phase 0; no fixed `Thread.Sleep` anywhere |
| `authenticationType` fix silently regressed during promotion | The Phase 0 test asserting anonymous connections are denied moves to `tests/` in Slice 5 and must stay green |

---

## 9. Definition of done

Per [06-project-plan.md § Definition of Done](../docs/docs/06-project-plan.md):

1. All 19 Phase 1 deliverables implemented.
2. All existing tests still pass (including every promoted Phase 0 test).
3. Phase 1 integration tests added and passing.
4. **Milestone:** `SampleChatApp.Api` connects to the proxy; a .NET `HubConnection` negotiates through the API and exchanges messages through the proxy end-to-end.
5. No unresolved TODO/FIXME in new code.
6. `spike/` deleted, `spike/findings/` preserved under `docs/`.
7. Documentation updates in §7 applied.
