# Phase 4 — Management & Observability: Implementation Plan

**Source of truth:** [06-project-plan.md § Phase 4](../docs/docs/06-project-plan.md), [03-protocol.md Part 3](../docs/docs/03-protocol.md#part-3-management-rest-api), [ADR-004](../docs/docs/07-adr/ADR-004-token-authority.md), [04-design.md](../docs/docs/04-design.md), [05-data-models.md](../docs/docs/05-data-models.md), [00-review-findings.md](../docs/docs/00-review-findings.md).

**Goal.** An operations team can inspect service state and send messages without a SignalR client, and can see what the service is doing through standard tooling.

**Milestone check.** Active connections, message throughput, and fan-out size are visible on a dashboard built against OTLP-exported metrics; an on-call engineer can broadcast a maintenance notice with `curl`.

Phases 1–3 built a service that is deliberately **payload-agnostic** (it forwards hub-protocol bytes it never constructs, apart from handshake/`Ping`/`Close`) and deliberately **node-local on every hot path** (fan-out reads `ILocalTransportRegistry`; `/healthz` answers from a cached field, never grain I/O). Phase 4 is the first phase that pushes against both of those properties: the management API has to *originate* hub messages, and both the management API and the metrics have to describe state that spans the cluster. The decisions below are mostly about doing that without dragging cluster I/O or payload parsing onto paths that Phase 3 spent an entire phase keeping clean.

---

## 1. Preconditions — what Phases 1–3 already settled

| Established | Consequence for Phase 4 |
|---|---|
| `ITokenService.IssueManagementToken` / `Validate(…, SwitchboardTokenType.Management)` already exist and are tested | Management auth is **wiring**, not new crypto. `ManagementSigningKey`/`…Fallback`/`ManagementAudience` are already on `SwitchboardOptions`; `TokenCommand` already emits `--role management` |
| Every authenticated endpoint hand-rolls `Authorization: Bearer` + `ITokenService.Validate` ([ServerConnectionEndpoint.cs:25](../src/Keryhe.Switchboard.Server/ServerConnections/ServerConnectionEndpoint.cs:25)) | One established pattern to match — see **finding 7** before reaching for `AddJwtBearer` |
| `IConnectionRegistry.GetAllAsync`/`GetGroupMembersAsync`/`GetUserConnectionsAsync` were explicitly kept off the hot path for "diagnostics and the Phase 4 management API" (**D14**) | Phase 4 is their intended caller — but their own XML docs say *one grain call per connection*, which is a pagination requirement, not a footnote (**D27**) |
| `IReadinessProbe` — refresh out of band, answer from a plain field | The template for every cluster-wide number Phase 4 exposes (**D24**, **D27**) |
| Every Phase 3 substitution keeps `UseOrleansCluster = false` fully alive | Every management endpoint and every metric must work in **both** modes, and be tested in both |
| `ServerEnvelope` `[Key(0..11)]` and grain state `[Id(n)]` are append-only wire contracts | Phase 4 needs no `ServerEnvelope` change (**D26**); the grain additions in **D27** are appends |
| `ClientFrameWriter`'s charter: "the service's *only* source of client-facing hub-protocol bytes it must construct itself… widening this type into a general hub-protocol writer is a review failure" | **D22** is where that charter gets tested. It is not widened; a second, equally narrow type is added beside it |
| Phase 3 Slice 7's `AddToGroup` bug: node-local group mutation without an owner-node forward is silent | **D23** — the management API must not re-derive that logic; it must call the same code |

### New framework facts verified while writing this plan

Checked empirically against this repo's dependency set and .NET 10, not assumed from the roadmap. Each contradicts something a reasonable implementer would otherwise assume.

1. **`System.Diagnostics.Metrics` needs no package reference.** A fresh `net10.0` classlib with **zero** `PackageReference` entries compiles `new Meter(...)`, `CreateCounter<long>`, `CreateHistogram<T>`, `CreateObservableGauge(Func<IEnumerable<Measurement<long>>>)` — `Build succeeded. 0 Warning(s).` So instrumentation can live in `Keryhe.Switchboard.Core` without breaking its "no external dependencies beyond BCL" rule; only the *export* needs OpenTelemetry (**D24**).

2. **OpenTelemetry 1.17.0 is conflict-free here.** `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Instrumentation.AspNetCore`, and `OpenTelemetry.Exporter.OpenTelemetryProtocol` — all **1.17.0**, the current latest — added to the real `Keryhe.Switchboard.Server` project build the full solution at `0 Warning(s), 0 Error(s)`: no conflict with MessagePack 3.1.8, Orleans 10.2.2, Npgsql 10.0.3, or `System.IdentityModel.Tokens.Jwt` 8.2.1, and no NU19xx advisories. (Change reverted; it belongs in Slice 4.) The roadmap's package table says "latest" — pin 1.17.0.

3. **A misconfigured OTLP endpoint is silent, not loud.** A host with an OTLP metric exporter pointed at a port with nothing listening started normally, exported on its interval, and shut down in ~14 ms including `Dispose` — **no exception, no `ILogger` warning, nothing on stdout.** OTel exporter failures go to OTel's own `EventSource`, not the application's logging pipeline. A typo in the endpoint therefore produces a service that looks perfectly healthy and emits nothing at all. Two consequences: the milestone cannot be "it builds and starts" (it must be "a collector actually received the data"), and startup must log the configured endpoint explicitly.

4. **`MessagePackHubProtocol` silently encodes `JsonElement` arguments as empty maps.** Verified: `new InvocationMessage("ReceiveMessage", args)` where `args` are `JsonElement`s taken straight from a REST body writes `96 80 80 80 80 80 80 …` — six empty fixmaps where six real arguments should be — **with no exception**. `JsonHubProtocol` handles the same arguments perfectly, so a JSON-only test suite would never notice. Mapping each `JsonElement` to CLR primitives (`string` / `long` / `double` / `bool` / `null` / `object?[]` / `Dictionary<string, object?>`) first produces correct bytes under *both* protocols (verified). This is the single most likely silent defect in the whole phase (**D22**).
   *A trap on top of the trap:* `e.TryGetInt64(out var l) ? l : e.GetDouble()` has best-common-type `double`, so every integer silently arrives as a float. Cast to `object` explicitly. (Cost me one wrong reading of the byte dump while verifying the above.)

5. **There is no cluster-wide hub or node directory.** `IHubRegistry.GetAllHubs()` and `ILocalTransportRegistry.GetKnownHubNames()` are both explicitly node-local, and [`INodeRegistryGrain`](../src/Keryhe.Switchboard.Orleans/Grains/INodeRegistryGrain.cs) is `Register`/`Unregister`/`GetInternalUrl(nodeId)` with **no way to enumerate nodes**. So `GET /api/v1/health` answered on node A cannot see a hub that only node B knows about, and there is no way to answer "which hubs exist" at all. This is not an optimization gap; it is a missing capability that two Phase 4 endpoints require (**D27**).

6. **`IHubGrain` has no counts API.** The only membership read is `GetConnectionIdsAsync()`, which returns the entire list — so "how many clients does this hub have cluster-wide" currently means transferring every connection id across the cluster to call `.Count` on it (**D27**).

7. **`Microsoft.AspNetCore.Authentication.JwtBearer` 10.0.0 is referenced by `Keryhe.Switchboard.Server` and used nowhere.** `grep` for `AddAuthentication`/`AddAuthorization`/`JwtBearer` across `src/` returns nothing — every authenticated endpoint validates by hand through `ITokenService`. Phase 4 is the point at which that package is either finally used or removed; leaving an unused auth package referenced while hand-rolling auth beside it is exactly the kind of thing that makes a later reader assume authentication middleware is in play when it is not (**D21**).

8. **`ClientConnectionState.LastSeen` is written once, at registration, and never refreshed.** The per-request update lands on `ClientConnection.LastSeen` ([ClientConnectionLifecycle.cs:116](../src/Keryhe.Switchboard.Server/ClientConnections/ClientConnectionLifecycle.cs:116)), a different object; the registry/grain copy is frozen at connect time. [03-protocol.md Part 3](../docs/docs/03-protocol.md#part-3-management-rest-api)'s connection listing does not include it, and Phase 4 must not add it — an "idle since" column that always equals `connectedAt` is worse than no column. Either fix the write or leave the field out of the API; this plan leaves it out and records the discrepancy.

---

## 2. Decisions

Nine decisions, **D21–D29**, continuing D1–D6 (Phase 1), D7–D13 (Phase 2), and D14–D20 (Phase 3), so a code comment saying "plan decision D18" stays unambiguous.

### D21 — `Keryhe.Switchboard.Management` is a library mapped into the existing host, and it depends on `Protocol`, not just `Core`

The roadmap's dependency graph says `Management ← Core + ASP.NET Core`. It cannot be: the health endpoint needs `IHubRegistry` (server-connection inventory) and the send endpoints need hub-protocol writing, both of which live in `Keryhe.Switchboard.Protocol`. **Doc correction:** `Management ← Core + Protocol + ASP.NET Core`, with `Server` taking the project reference and doing the `MapManagementApi(app)` call — the direction the roadmap already has.

The management API is **not** a separate process or a separate host. It is a route group (`/api/v1`) mapped into the same `WebApplication` as every other endpoint, so it inherits the existing CORS, options, DI, and Orleans wiring for free.

**It must never reference `Keryhe.Switchboard.Orleans`.** Cluster-wide reads go through a new Core-level abstraction with two implementations, exactly like every other Phase 3 substitution (**D27**) — otherwise the layering inverts and the management API stops working in single-node mode.

**Fail closed by absence, not by 401.** If `ManagementSigningKey` is not configured, the `/api/v1` routes are **not mapped at all** (404, no surface). Add `EnableManagementApi` (default `true`) and a startup validation refusing to boot when it is `true` with no key — matching the existing fail-fast posture for `PublicUrl` and Pattern A's allowlist. A 401-only defence would leave an unauthenticated admin surface answering requests on a public listener with a key nobody set.

**On finding 7:** keep hand-rolled validation via `ITokenService` — it matches every other endpoint in the service, keeps the three-token model in one place (ADR-004), and avoids configuring an authentication scheme that only one route group uses. Then **remove** the unused `Microsoft.AspNetCore.Authentication.JwtBearer` reference in the same slice, so the decision is visible in the csproj rather than implied.

**401, not 403, for a wrong-type token.** A server access token presented to `/api/v1` fails validation at the audience and signing key before role is ever considered — it is cryptographically indistinguishable from a garbage token, so synthesizing a 403 would mean validating it a second time as a server token purely to produce a nicer status code. [03-protocol.md Part 3](../docs/docs/03-protocol.md#part-3-management-rest-api) should say 401 explicitly, since "server access tokens explicitly rejected" reads like it implies 403.

### D22 — The management API is the service's first payload *producer*; the widening is one new narrow type, and `JsonElement` must never reach MessagePack

`POST /api/v1/hubs/{hub}/send` takes `{target, arguments}` and must produce hub-protocol `Invocation` bytes. That is payload *construction*, which the service has deliberately never done outside `ClientFrameWriter`'s three-message charter.

**Recommendation:** leave `ClientFrameWriter` untouched and add `ManagementInvocationWriter` beside it in `Keryhe.Switchboard.Protocol/Framing/`, with its own equally narrow charter comment: *invocation messages originating from the management REST API only; the service still never constructs hub messages on any client- or server-driven path.* It produces a `payloadsByProtocol` dictionary covering exactly `json` and `messagepack` (Phase 2 **D7**'s shape), because a management broadcast has no idea what protocols its recipients negotiated — the same reason app-server fan-out carries `Payloads`.

**The argument mapping is the load-bearing part (finding 4).** Map every `JsonElement` from the request body to CLR primitives before handing it to either protocol. Skipping this does not fail — it silently delivers `{}` for every argument to every MessagePack client while JSON clients look perfect.

**Management sends bypass app servers entirely.** Hub code does not run, hub filters do not run, `IHubContext` is not involved — the service injects the message straight into fan-out. This matches Azure SignalR's REST API semantics and is the predictable support question ("why didn't my hub method see it?"), so it belongs in the protocol doc, not just here.

### D23 — Management group add/remove calls the *same* code as the `add_to_group` envelope, extracted — never re-implemented

`PUT`/`DELETE /api/v1/hubs/{hub}/groups/{group}/connections/{id}` do exactly what [RoutingServerEnvelopeDispatcher.cs:59-88](../src/Keryhe.Switchboard.Server/ServerConnections/RoutingServerEnvelopeDispatcher.cs:59) already does: update the registry, then update the **owning node's** local index — via `IBackplane.PublishAddToGroupAsync` when the connection is not local.

That second half is the Phase 3 Slice 7 bug, and its failure mode is total silence: the client joins the group and simply never receives a group message, with no exception and no log a happy-path test would trip over. A hand-written second copy in the management controller would reproduce it exactly.

**Recommendation:** extract the body into `GroupMembershipService` (interface + implementation) in `Keryhe.Switchboard.Core` — every dependency it has (`IConnectionRegistry`, `ILocalTransportRegistry`, `IBackplane`) is already a Core interface — and have both the envelope dispatcher and the management endpoint call it. The extraction is behavior-preserving and belongs in the same slice as the endpoints, with the existing dispatcher tests as the net.

*(Alternative considered: put it in `Management` and let `Server` use it, since `Server → Management` is the reference direction anyway. Rejected — an assembly named "Management" owning the envelope dispatcher's routing logic is backwards for the next reader.)*

### D24 — Instrumentation is BCL `Meter` in `Core`; OpenTelemetry lives only in the host, and only when an endpoint is configured

Findings 1–3. A single `SwitchboardMetrics` singleton in `Keryhe.Switchboard.Core` owns the `Meter` and every instrument; call sites take it by DI. No project outside `Keryhe.Switchboard.Server` gains an OpenTelemetry reference, and Core's "BCL only" rule survives.

**Gauges are node-local observable gauges.** `signalr.client_connections.active` and `signalr.server_connections.active` are read from `ILocalTransportRegistry` / `IHubRegistry` — this node's own numbers, tagged with the node id as a resource attribute, summed by the backend across nodes. An `ObservableGauge` callback that does grain I/O is the `/healthz` mistake repeated at the collection interval, on every node, forever.

**Export is opt-in.** Wire `AddOpenTelemetry()` only when `OtlpEndpoint` is configured, and log one line at startup naming the endpoint (finding 3 — otherwise a typo is indistinguishable from working). With no endpoint configured, the `Meter` still exists and still records, so in-process tests can observe it via `MeterListener` with no collector and no exporter anywhere.

No Prometheus scrape endpoint — the roadmap already says so, and it stays true.

### D25 — `signalr.message.latency` as specified is not observable from a proxy; replace it with two service-side histograms

The roadmap asks for a histogram of "client→server round trip". Measuring that requires correlating an `Invocation` with its `Completion`, which requires reading the `invocationId` **out of the payload** on the hot path — precisely what the service refuses to do: `HubMessageClassifier` reads only the type discriminator and says in its own doc comment that widening it into a general parser is a review failure. And the correlation would be wrong anyway for the cases that matter most (streaming produces many `StreamItem`s per invocation; a fan-out send has no invocation at all).

**Recommendation — measure the proxy's own contribution, which is the part an operator can act on:**

- `signalr.message.inbound_duration` — client frame read → written to the assigned server connection (including any cross-node hop, tagged with whether one occurred).
- `signalr.message.outbound_duration` — envelope received from an app server → written to the target client transport's channel.

Both are honest, both are cheap, and together they answer "is Switchboard adding latency?" — the actual operational question. **This is a roadmap doc correction, in the same category as Phase 3's D19.** True end-to-end round-trip latency stays a client-side measurement, and Phase 5's benchmark suite is where it belongs.

### D26 — Tracing: negotiate and connect always; per-message spans off by default; no `ServerEnvelope` change

Spans for negotiate and client connect are cheap (one per connection) and genuinely useful. A span **per routed message** at broadcast fan-out rates is the cardinality equivalent of doing grain I/O in `/healthz` — recommend it exists behind `TraceMessageRouting` (default `false`), with the option's own doc comment saying why.

**No trace-context propagation into `ServerEnvelope` in Phase 4.** Adding `[Key(12)] TraceParent` is a legal append, but it is a wire contract shared with every already-deployed Connector, and the benefit — parent/child linkage into app-server spans — is obtainable more cheaply by tagging both sides with `connectionId` and correlating in the backend. Revisit if Phase 5's compatibility work or a real operational need shows the correlation-by-attribute approach is insufficient; record the trigger rather than leaving it as an open "maybe".

### D27 — Cluster-wide operator reads get a Core abstraction with two implementations, plus three grain additions; list endpoints are paginated

Findings 5 and 6 mean two Phase 4 endpoints cannot be built from what exists.

**Recommendation — `IClusterInventory` in `Keryhe.Switchboard.Core`**, substituted like every other Phase 3 interface: `LocalClusterInventory` (Registry — the node *is* the cluster) and `OrleansClusterInventory` (Orleans). This is what keeps `Management` free of an Orleans reference (**D21**) and what keeps both deployment modes real.

It needs three additions, all appends under **D20**'s `[Alias]`/`[Id(n)]` rules:

1. `INodeRegistryGrain.GetAllNodesAsync()` — enumerate nodes. The grain already holds the whole map in one activation; only the read method is missing.
2. **A cluster-wide hub directory.** Recommend carrying it on the *same* grain: `NodeRegistryPublisherService` already publishes this node's `InternalUrl` on a cadence, so have it publish the node's known hub names alongside (union of `IHubRegistry.GetAllHubs()` and `ILocalTransportRegistry.GetKnownHubNames()` — **both**, for exactly the reason the Phase 3 Slice 7 bug note gives). No new grain, no new hosted service, and the freshness bound is the publisher's existing interval.
3. `IHubGrain.GetStatsAsync()` — client-connection count and server-connection count as numbers, so a health check does not transfer every connection id to call `.Count` on it (finding 6).

**`GET /api/v1/hubs/{hub}/connections` is paginated, mandatorily.** `GetAllAsync` is one grain call per connection — its own XML doc says so. Take `?limit=` (default 100, hard max 1000) and an opaque continuation token; `totalCount` in the response is the hub's membership count (one call), not the page size. An unpaginated version of this endpoint is a self-inflicted outage on the first cluster with real traffic, and it will be discovered by an operator, in production, at the worst moment.

**`GET /api/v1/health` may do real cluster I/O** — it is an authenticated operator endpoint called at human rates, not a load-balancer probe. `/healthz` stays exactly what Phase 3 made it: public, cached, no topology detail. Do not merge them, and do not let the detailed endpoint acquire a load-balancer caller.

### D28 — The deferred D3/D4 metrics are re-scoped, because what they were deferred *from* no longer exists

`signalr.envelopes.unrouted` was deferred from Phase 1 as "unrouted group/user envelopes" — a counter for **D3**'s deliberate log-and-no-op, which Phase 2 implemented. Emitting a metric under that name today would count nothing.

**Recommendation:** keep the name, re-scope it to the drops that are real now, tagged by reason — each of these is already a `LogWarning` with no metric behind it:

- unknown connection on `RouteClientMessageAsync`
- assigned server connection gone
- fan-out skipped: no payload for the target's negotiated protocol (**D7**)
- cross-node message dropped: no node subscribed for that hub (the Phase 3 Slice 7 failure mode)

That last one is worth the whole metric on its own — it was one of two silent bugs the Phase 3 milestone caught, and a counter on it turns the next occurrence into an alert instead of an investigation.

**`signalr.pending_connections.active` becomes a counter pair, not a gauge.** A gauge needs a live count, which in Orleans mode means enumerating per-token grains — a directory that does not exist and that **D19** deliberately avoided building. Emit `signalr.pending_connections.created` / `.consumed` / `.expired` instead: derivable in **both** modes from the existing `IPendingConnectionStore` call sites with zero new state, and the in-flight figure is the difference, computed in the backend. Strictly more information than the gauge (it distinguishes "expiring" from "never arriving") for strictly less machinery.

### D29 — The management API is reachable on the same listener, with a network allowlist in front of it

The API is mapped into the same Kestrel app as client traffic (**D21**), which in a typical deployment means it answers on the internet-facing listener. The management token is the real control, but a single leaked or long-lived token then reaches an admin surface from anywhere.

**Recommendation:** add `ManagementAllowedNetworks` (default empty = no network restriction, token only) evaluated with the same `System.Net.IPNetwork` matcher **D11** already uses for `TrustedProxyNetworks` — including its IPv4-mapped-IPv6 normalization, which is already written and tested. Cheap, consistent, defence in depth, and the operations guide can then recommend a concrete value instead of hand-waving about network policy.

Deliberately **not** a second Kestrel listener on a separate port: that changes the deployment topology, the container port mapping, and the health-probe story, for a benefit an allowlist already provides.

---

## 3. Target layout

One new project — `Keryhe.Switchboard.Management` — created now that it has contents, per the roadmap's "don't scaffold empty placeholder projects" guidance. Add it to `Switchboard.sln`.

```
src/Keryhe.Switchboard.Management/                   # new — Core + Protocol + ASP.NET Core (D21)
  ManagementEndpoints.cs                             # /api/v1 route group, mapped by the host
  ManagementAuth.cs                                  # bearer extraction + ITokenService.Validate + D29 allowlist
  Models/ManagementDtos.cs                           # send request, connection listing, health response
  ManagementApiExtensions.cs                         # AddSwitchboardManagement() / MapSwitchboardManagement()

src/Keryhe.Switchboard.Core/
  SwitchboardMetrics.cs                              # D24 — the Meter and every instrument (BCL only, finding 1)
  GroupMembershipService.cs                          # D23 — extracted from RoutingServerEnvelopeDispatcher
  IClusterInventory.cs                               # D27 — cluster-wide reads, substituted per deployment mode
  Models/SwitchboardOptions.cs                       # + EnableManagementApi, ManagementAllowedNetworks,
                                                     #   OtlpEndpoint, TraceMessageRouting

src/Keryhe.Switchboard.Protocol/Framing/
  ManagementInvocationWriter.cs                      # D22 — the only payload-producing type added

src/Keryhe.Switchboard.Registry/
  LocalClusterInventory.cs                           # D27 — single-node: this node is the cluster

src/Keryhe.Switchboard.Orleans/
  OrleansClusterInventory.cs                         # D27
  Grains/INodeRegistryGrain.cs, NodeRegistryGrain.cs # + GetAllNodesAsync, hub directory (append-only [Alias]/[Id])
  Grains/IHubGrain.cs, HubGrain.cs                   # + GetStatsAsync
  NodeRegistryPublisherService.cs                    # also publishes this node's known hub names

src/Keryhe.Switchboard.Server/
  Program.cs                                         # MapSwitchboardManagement, OTel wiring (D24), startup validation

docs/docs/10-operations.md                           # new — the operations guide deliverable
```

**Package changes:** `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, all **1.17.0** (verified conflict-free and audit-clean, finding 2), in `Keryhe.Switchboard.Server` only. **Removed:** `Microsoft.AspNetCore.Authentication.JwtBearer` (finding 7, **D21**). OpenAPI comes from ASP.NET Core's built-in `AddOpenApi()` — no Swashbuckle dependency.

---

## 4. Slices

Each slice ends runnable and independently testable. Ordering puts the API before the telemetry because the API is where the silent-correctness risks are (**D22**, **D23**), and telemetry that describes endpoints that do not exist yet cannot be verified against anything.

### Slice 1 — Project, auth, and group membership

- `Keryhe.Switchboard.Management` scaffolded, added to the solution, mapped by `Server` behind `EnableManagementApi` + the "not mapped without a signing key" rule (**D21**).
- `ManagementAuth`: bearer extraction, `ITokenService.Validate(…, Management)`, `ManagementAllowedNetworks` (**D29**). `Microsoft.AspNetCore.Authentication.JwtBearer` removed.
- `GroupMembershipService` extracted from `RoutingServerEnvelopeDispatcher` (**D23**), used by both it and the new `PUT`/`DELETE` group endpoints.

**Gate:** a management token drives the group endpoints; a **server** access token gets 401 and a management token gets 401 against `/server/{hub}` (both directions asserted — ADR-004's whole point); a request from outside a configured `ManagementAllowedNetworks` is rejected with the token still valid. `EnableManagementApi = true` with no `ManagementSigningKey` fails startup with `OptionsValidationException`; with the feature off, `/api/v1/...` is **404, not 401**. All existing dispatcher tests pass unchanged through the extraction. **The cross-node case is a gate, not a nice-to-have:** adding a connection owned by *another* node to a group, through the management API, and receiving a subsequent group message on it — the Phase 3 Slice 7 bug reproduced from the new caller.

### Slice 2 — Send endpoints and payload construction (**D22**)

- `ManagementInvocationWriter` with the `JsonElement` → CLR primitive mapping (finding 4), producing `payloadsByProtocol` for `json` and `messagepack`.
- `POST /api/v1/hubs/{hub}/send`, `…/users/{userId}/send`, `…/groups/{group}/send` → `IMessageRouter.BroadcastAsync` / `SendToUserAsync` / `SendToGroupAsync`, `202 Accepted`.

**Gate:** two real `HubConnection`s — one JSON, one MessagePack — both receive a management broadcast and both see **correct argument values**, including a nested object, an integer, a bool, and a null. This is the finding-4 regression test; a JSON-only assertion here passes against completely broken MessagePack output. Prove the hub method was never invoked on the app server (assert absence). Send to a hub with no connections returns 202, not an error. Both deployment modes; in clustered mode, a broadcast issued against node A reaches a client on node B.

### Slice 3 — Cluster-wide reads (**D27**)

- `IClusterInventory` + `LocalClusterInventory` + `OrleansClusterInventory`; `INodeRegistryGrain.GetAllNodesAsync` and the hub directory published by `NodeRegistryPublisherService`; `IHubGrain.GetStatsAsync`.
- `GET /api/v1/hubs/{hub}/connections`, paginated (`limit`, continuation token, `totalCount`).
- `GET /api/v1/health` — per-hub server-connection counts and the client-connection total, cluster-wide.

**Gate:** in clustered mode, a connection established on node B appears in a listing requested from node A, and a hub known only to node B appears in `/api/v1/health` requested from node A (finding 5 — this fails outright without the directory). Pagination is pinned by a test with more connections than one page and an assertion that the pages are disjoint and complete. `/healthz` is **byte-identical** to its Phase 3 behavior — same body, same cached path, no new topology detail — asserted, not assumed (**D27**). The `[Id(n)]` ordering test from Phase 3 is extended to the new grain state.

### Slice 4 — Metrics (**D24**, **D25**, **D28**)

- `SwitchboardMetrics` in Core; counters, histograms, and node-local observable gauges wired at their call sites.
- The full instrument set: `client_connections.active`, `server_connections.active`, `messages.routed`, `broadcast.fan_out_size`, `message.inbound_duration` / `message.outbound_duration` (**D25**), `envelopes.unrouted` by reason (**D28**), `pending_connections.created`/`.consumed`/`.expired` (**D28**).
- OTLP export in `Program.cs`, gated on `OtlpEndpoint`, with a startup log line naming it (finding 3).

**Gate:** an in-process `MeterListener` observes every instrument during a real end-to-end flow — no collector, no exporter — including a `broadcast.fan_out_size` recording that matches the actual recipient count and an `envelopes.unrouted{reason=…}` increment induced by routing to a connection that has disconnected. With `OtlpEndpoint` unset, no OpenTelemetry pipeline is constructed and no exporter thread exists. Gauges are asserted to be node-local: two nodes each report their own connection count, not the cluster total (double-counting here is silent and would corrupt every dashboard built on it).

### Slice 5 — Tracing and structured logging (**D26**)

- Activity spans for negotiate and client connect; per-message spans behind `TraceMessageRouting` (default off).
- `ILogger` → OTLP alongside traces and metrics, so all three signals land in one backend; connection lifecycle, routing errors, and server-connection health changes reviewed for structured (not interpolated) fields.

**Gate:** an in-process `ActivityListener` sees a negotiate span and a connect span with `hub`/`connectionId`/`node.id` attributes, and sees **no** per-message spans with the default configuration (assert absence — the cardinality risk is the whole point of the flag). Logs carry structured properties, verified through a capturing provider rather than by reading strings.

### Slice 6 — OpenAPI, operations guide, and the milestone

- `AddOpenApi()` + the generated document covering every `/api/v1` route.
- `docs/docs/10-operations.md`: the three token types and their independent signing keys, CLI generation, the two-key rotation procedure using the `…Fallback` keys, secret storage, `ManagementAllowedNetworks` guidance, and what each metric means operationally.
- The doc corrections in §7.

**Gate — the milestone, run against a real collector (finding 3 makes anything less meaningless):** an OTLP collector container (docker CLI, the same throwaway-container pattern `PostgresContainerFixture` already uses) receives `client_connections.active`, `messages.routed`, and `broadcast.fan_out_size` from a running two-node cluster, and the assertion is on the **collector's received data**, not on the service having started. A plain `curl` with a CLI-generated management token broadcasts a maintenance notice that a live `HubConnection` receives.

---

## 5. Testing strategy

Phases 2–3 discipline carries forward (real clients as ground truth, assert absence, bound every wait, run the suite in **both** `UseOrleansCluster` modes). Four additions specific to this phase:

- **Every management endpoint is tested in both deployment modes.** The single-node path is the recommended deployment for most users and is the one that will rot while attention is on the cluster-wide reads.
- **Telemetry is asserted in-process, exported once.** `MeterListener`/`ActivityListener` for per-instrument assertions in the unit suite (fast, no container, no network); exactly one integration test proves bytes actually reach a collector. Finding 3 means "the exporter is configured" proves nothing on its own.
- **The MessagePack path is a first-class assertion everywhere payloads are produced.** Finding 4's failure mode is invisible to a JSON-only test and produces no exception. Any test of a send endpoint that does not have a MessagePack client on the other end is not testing the risky half.
- **Cross-node is the default for management tests, as it was for Phase 3's routing tests.** Both of Phase 3's silent bugs were "the connection is not on the node you are standing on"; every management operation naming a `connectionId` has the same exposure from a new caller.

---

## 6. Deliverable ↔ slice mapping

Every checkbox in [06-project-plan.md § Phase 4](../docs/docs/06-project-plan.md):

| Deliverable | Slice |
|---|---|
| `Keryhe.Switchboard.Management` REST API (all Part 3 endpoints) | 1 (group), 2 (send), 3 (list, health) |
| Management API auth: `ManagementSigningKey`, `role: management`, server tokens rejected | 1 |
| OpenTelemetry metrics via OTLP — the seven listed instruments | 4 |
| `signalr.envelopes.unrouted` / `signalr.pending_connections.active` (deferred from Phase 1 D3/D4) | 4 — **re-scoped, see D28** |
| `signalr.message.latency` | 4 — **replaced by two service-side histograms, see D25** |
| OpenTelemetry tracing: negotiate, client connect, message route | 5 (message-route spans off by default, **D26**) |
| Structured logging exported via OTLP | 5 |
| `/healthz` public + `GET /api/v1/health` authenticated | 3 (`/healthz` unchanged from Phase 3 — pinned, not re-implemented) |
| Swagger/OpenAPI spec for the management API | 6 |
| Operations guide: three token types, generation, rotation, secret storage | 6 |

Not on the roadmap's list but required by it: `IClusterInventory` plus the node/hub directory and `GetStatsAsync` (**D27**, findings 5–6) — without them two of the listed endpoints cannot be answered at all in clustered mode; the `GroupMembershipService` extraction (**D23**), without which the management group endpoints reproduce a bug Phase 3 already paid for; and the `JsonElement`→primitive mapping (**D22**, finding 4), without which the send endpoints silently deliver empty objects to every MessagePack client.

---

## 7. Documentation updates due at the end of Phase 4

- **[06-project-plan.md](../docs/docs/06-project-plan.md)** — tick Phase 4; correct `Management ← Core + Protocol + ASP.NET Core` (**D21**); pin OpenTelemetry at 1.17.0 (finding 2); correct the metric list per **D25**/**D28**; note what Phase 5 inherits.
- **[03-protocol.md Part 3](../docs/docs/03-protocol.md#part-3-management-rest-api)** — pagination parameters and `totalCount` semantics on the connection listing; the argument-encoding rule and that arguments are JSON values mapped to hub-protocol primitives (**D22**); that management sends bypass hub code entirely; 401 (not 403) for a wrong-type token (**D21**); the network allowlist (**D29**); `GET /api/v1/health` in both deployment modes.
- **[04-design.md](../docs/docs/04-design.md)** — a new **§12 Management API** (auth, the endpoint set, `IClusterInventory`, why it holds no Orleans reference) and **§13 Observability** (the instrument set, node-local gauges, the sampling posture, why round-trip latency is not measurable here — **D25**).
- **[05-data-models.md](../docs/docs/05-data-models.md)** — management request/response DTOs; the new `SwitchboardOptions` fields; the new grain methods and state under the append-only `[Id(n)]`/`[Alias]` rules; **finding 8** — `ClientConnectionState.LastSeen` is written once and never refreshed, and is deliberately absent from the management API.
- **[ADR-004](../docs/docs/07-adr/ADR-004-token-authority.md)** — point its "consolidated operations guide" reference at the now-real `10-operations.md`.
- **New: [10-operations.md](../docs/docs/10-operations.md)** — the operations guide deliverable.
- **[00-review-findings.md](../docs/docs/00-review-findings.md)** — a Phase 4 results entry in the format Phases 0–3 use, including the two roadmap corrections (`signalr.message.latency`, the re-scoped D3/D4 metrics) and whatever the `MessagePackHubProtocol`/`JsonElement` work turns up in practice.
- **[CLAUDE.md](../CLAUDE.md)** — Project Status, the solution layout (Management is no longer "Phase 4 — not created yet"), and the observability notes.

---

## 8. Risks

| Risk | Mitigation |
|---|---|
| Management broadcasts arrive at MessagePack clients as empty objects | Verified failure mode (finding 4) with no exception; **D22** mandates the primitive mapping and Slice 2's gate requires a real MessagePack client asserting argument *values* |
| The management group endpoints silently no-op for connections owned by another node | **D23** forbids a second implementation; Slice 1's gate is explicitly the cross-node case — the exact Phase 3 Slice 7 bug, from a new caller |
| Telemetry is wired, looks fine, and exports nothing | Verified silent-failure mode (finding 3): a dead OTLP endpoint produces no exception and no log. Startup logs the endpoint; the milestone asserts on a real collector's received data |
| `GET /api/v1/hubs/{hub}/connections` melts a production cluster | One grain call per connection by construction (**D14**'s own doc comment); pagination is mandatory in **D27** with a hard cap, not advisory |
| Observable gauges do grain I/O on every collection interval, on every node | **D24** makes them node-local reads; Slice 4's gate asserts each node reports only its own count |
| Per-message spans are enabled by default and cost more than the routing they trace | **D26** defaults `TraceMessageRouting` off; Slice 5 asserts their *absence* under default configuration |
| The detailed health endpoint acquires a load-balancer caller and starts doing cluster I/O every two seconds | **D27** keeps the two endpoints separate and Slice 3 pins `/healthz` byte-identical to Phase 3; the operations guide says which is which and why |
| The management API answers unauthenticated on a public listener because a key was never configured | **D21** does not map the routes at all without a key, plus fail-fast startup validation — a 401 default would still be a live admin surface |
| `Management` acquires an Orleans reference and the single-node deployment quietly stops working | **D21**/**D27** put cluster reads behind `IClusterInventory`; the whole suite runs in both modes, as it has since Phase 3 |
| The service drifts into a general payload parser/producer now that one endpoint constructs messages | **D22** adds one narrowly-chartered type beside `ClientFrameWriter` rather than widening it; both charters are comments a reviewer can point at |
| The re-scoped metrics quietly diverge from what the roadmap promised, with nobody noticing the roadmap was wrong | **D25**/**D28** are recorded as doc corrections in §7 and land in `00-review-findings.md`, the same way D19's `connectionToken` correction did |

---

## 9. Definition of done

Per [06-project-plan.md § Definition of Done](../docs/docs/06-project-plan.md):

1. All 8 Phase 4 deliverables implemented (with **D25**/**D28**'s corrections applied and recorded, not silently substituted).
2. All existing tests still pass — the Phase 3 baseline of 195 unit + 3 integration tests — in **both** `UseOrleansCluster` modes.
3. Phase 4 tests added and passing, including the cross-node management-group gate, the MessagePack argument-value gate, and the real-collector export test.
4. **Milestone:** metrics from a running two-node cluster are received by a real OTLP collector and visible on a dashboard; a `curl` broadcast with a CLI-generated management token reaches a live client.
5. No unresolved TODO/FIXME in new code.
6. Documentation updates in §7 applied, including the new operations guide.
