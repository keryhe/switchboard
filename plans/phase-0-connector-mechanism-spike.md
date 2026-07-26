# Phase 0 — Connector Mechanism Spike: Implementation Plan

**Source of truth:** [06-project-plan.md § Phase 0](../docs/docs/06-project-plan.md), [04-design.md §8](../docs/docs/04-design.md) (negotiate interception), [04-design.md §11](../docs/docs/04-design.md) (inbound dispatch), [00-review-findings.md](../docs/docs/00-review-findings.md).

**Goal.** Prove the Connector's two unproven mechanisms against real ASP.NET Core 10 before Phase 1 commits to them:

- **A — Negotiate interception:** a `MatcherPolicy` can take over `MapHub<T>()`'s negotiate endpoint, return a redirect, and *keep* the hub's class-level `[Authorize]`.
- **B — Inbound dispatch:** `HubConnectionHandler<THub>` can be driven over a synthetic `ConnectionContext` (a `Pipe` pair) with no real client, with correct identity and lifecycle.

**Not disposable.** The policy skeleton, the synthetic-connection skeleton, and the test clients are promoted into `Keryhe.Switchboard.Connector` in Phase 1. Only the scaffolding (throwaway host, stub redirect target, hardcoded tokens, test hubs) is discarded.

**Non-goals.** No proxy service, no `ServerEnvelope`/MessagePack wire format, no real JWT issuance, no `IHubLifetimeManager` implementation beyond a recording stub, no transports other than WebSockets, no Orleans. Anything that requires the Switchboard service to exist is stubbed.

---

## 1. Preconditions

| Item | Status |
|---|---|
| .NET SDK 10 | ✅ present (`10.0.201`, also `10.0.100`) |
| Node / npm (for the `@microsoft/signalr` client) | ✅ present (`v22.17.0` / `11.4.2`) |
| Repo is a git repository | ❌ not initialised — run `git init` before writing code, since this output is carried into Phase 1 and the spike's value is in its recorded findings |

---

## 2. Spike layout

Kept out of the Phase 1 tree (`src/`, `tests/`) so promotion is an explicit, reviewed move rather than an accident of location.

```
spike/
├── Phase0.Spike.sln
├── Phase0.Spike.Connector/          # ← CARRY-FORWARD CODE (becomes Keryhe.Switchboard.Connector)
│   ├── Negotiate/
│   │   ├── SwitchboardNegotiateMatcherPolicy.cs
│   │   └── NegotiateRedirectDelegate.cs        # stub redirect body in the spike
│   └── Dispatch/
│       ├── SwitchboardClientConnectionContext.cs
│       ├── ConnectionFeatures.cs               # IConnectionUserFeature / Id / Items / Heartbeat impls
│       └── HubPipelineFactory.cs               # ConnectionBuilder → ConnectionDelegate, one per hub type
├── Phase0.Spike.Host/               # ← SCAFFOLDING (discarded in Phase 1)
│   ├── Program.cs                   # AddSignalR + MapHub + MVC controller + minimal API + stub target
│   └── Hubs/{TestHub,SecureHub,RejectingHub}.cs
├── Phase0.Spike.Tests/              # xunit; assertions become the Phase 1 integration test seed
└── Phase0.Spike.JsClient/           # node script driving @microsoft/signalr against the host
    ├── package.json
    └── redirect-check.mjs
```

`Phase0.Spike.Connector` and `Phase0.Spike.Tests` both need `<FrameworkReference Include="Microsoft.AspNetCore.App" />` — every seam in play (`MatcherPolicy`, `ConnectionBuilder`, `HubConnectionHandler<T>`, the connection features) ships in the shared framework, not in a NuGet package.

---

## 3. Workstream A — Negotiate interception

### A0 — API recon (do this first; it is the biggest unknown)

Before writing the policy, confirm each framework fact below against the .NET 10 SDK actually installed. Fastest route: a scratch console/test that references `Microsoft.AspNetCore.App` and simply *compiles* against each symbol, plus a runtime dump of the negotiate endpoint's metadata.

| Fact to verify | How | If false |
|---|---|---|
| `Microsoft.AspNetCore.Http.Connections.NegotiateMetadata` is public and reachable | compile-time reference | find the actual marker via the dump below |
| `MapHub<T>("/testHub")` attaches that marker to the `/testHub/negotiate` endpoint | inject `EndpointDataSource`, dump every endpoint's `DisplayName` + `Metadata` types at startup | match on the route pattern's trailing `negotiate` segment instead — record the compromise |
| `CandidateSet.ReplaceEndpoint(int, Endpoint, RouteValueDictionary)` is public | compile-time reference | fall back to Workstream A's fallback (A6) |
| `MapHub<T>()` copies hub-class attributes into endpoint metadata | assert `AuthorizeAttribute` present in the dump for `SecureHub`'s negotiate endpoint | the `[Authorize]` preservation strategy changes — escalate, this is the [risk-register](../docs/docs/06-project-plan.md) "silent drop" item |

**Done when:** a checked-in `findings/negotiate-endpoint-dump.txt` records the exact metadata types on the negotiate endpoint for both `TestHub` and `[Authorize] SecureHub`.

### A1 — Host scaffolding

Minimal host: `AddSignalR()`, `MapHub<TestHub>("/testHub")`, `MapHub<SecureHub>("/secureHub")` (hub class decorated `[Authorize]`), JWT-bearer auth with a hardcoded dev key, a `MapGet("/api/ping")` minimal API, one MVC controller, and a **stub redirect target**: `POST /stub/{hub}/negotiate` returning a canned step-2 response plus `GET /stub/{hub}` accepting a WebSocket, completing the SignalR handshake, and holding the socket open.

**Done when:** the host runs, `TestHub` negotiates normally (no policy registered yet), and a .NET `HubConnection` connects to it end-to-end. This is the *control* case — everything after this is measured against it.

### A2 — `SwitchboardNegotiateMatcherPolicy`

`MatcherPolicy` + `IEndpointSelectorPolicy`:

- `AppliesToEndpoints` → true only for endpoints carrying the marker confirmed in A0.
- `ApplyAsync(HttpContext, CandidateSet)` → for each valid candidate, build a replacement `Endpoint` **from the original's `Metadata` collection** (copy it wholesale; do not construct a bare endpoint) whose `RequestDelegate` writes `{ "url": "<stub target>/{hub}", "accessToken": "<hardcoded>" }` as `application/json`, then `ReplaceEndpoint(i, replacement, values)`.
- Hub name derived from the matched route, not per-hub configuration.
- Registered via `services.TryAddEnumerable(ServiceDescriptor.Singleton<MatcherPolicy, SwitchboardNegotiateMatcherPolicy>())`.

**Done when:** `POST /testHub/negotiate` returns the redirect JSON and the framework's negotiate delegate demonstrably never ran (no `connectionId`/`availableTransports` in the body; assert on the absence, not just the presence of `url`).

### A3 — `[Authorize]` preservation (the load-bearing test)

Two tests against `/secureHub/negotiate` **with the policy registered**:

1. No bearer token → **401**, and the redirect body is *not* returned.
2. Valid bearer token → 200 with the redirect body.

Plus a regression guard: a test that constructs the policy's replacement endpoint and asserts its `Metadata` contains the `AuthorizeAttribute` copied from the original. This is the only surviving enforcement point for hub-class authorization ([04-design.md §8](../docs/docs/04-design.md)); a bare-endpoint replacement drops it silently with no error.

### A4 — Ordering and isolation

- Register a second, unrelated `IEndpointSelectorPolicy` at `Order` values above and below the Switchboard policy; assert the redirect still wins in both arrangements.
- Assert the **transport** endpoint (`GET /testHub`) is untouched — it still routes to `HttpConnectionDispatcher`.
- Assert `/api/ping` and the MVC controller route behave identically with and without the policy registered.

### A5 — End-to-end redirect with real clients

- **.NET:** `HubConnection` built with `.WithUrl("http://localhost:PORT/testHub")` negotiates, receives the redirect, re-negotiates at the stub target, and opens a WebSocket. Assert `State == Connected` and that the stub target observed both the step-2 negotiate and the socket upgrade.
- **JS:** `Phase0.Spike.JsClient/redirect-check.mjs` does the same with an unmodified `@microsoft/signalr`, exits non-zero on failure. Runs against the host started out-of-process (`dotnet run --project spike/Phase0.Spike.Host`).

**No SignalR fork and no reflection into framework internals** — if either client only works via reflection, the mechanism has failed and A6 applies.

### A6 — Fallback (only if A0/A2 fails)

Do not map the connector's hubs with `MapHub<T>()`. Instead register an explicit `MapPost("/{hub}/negotiate", …)` that the app author calls, sidestepping endpoint-selector policies entirely. Two things must be proven for the fallback to count:

- No `AmbiguousMatchException` against the routes the app still maps.
- `[Authorize]` is re-established explicitly (`.RequireAuthorization()` mirroring the hub's attributes) — the metadata copy is *not* automatic here, so the A3 tests must still pass.

Taking this path is a **design change to [04-design.md §8](../docs/docs/04-design.md)** and must be written up before Phase 1 planning.

---

## 4. Workstream B — Inbound dispatch

### B0 — API recon

| Fact to verify | Notes |
|---|---|
| `ConnectionBuilder` + `UseConnectionHandler<HubConnectionHandler<THub>>()` are public and compose | `HubConnectionHandler<THub> : ConnectionHandler` is public per [04-design.md §11](../docs/docs/04-design.md) |
| `HandshakeProtocol.WriteRequestMessage(HandshakeRequestMessage, IBufferWriter<byte>)` is public | needed for handshake synthesis |
| `ConnectionItems` is public | [04-design.md §11](../docs/docs/04-design.md) shows `new ConnectionItems()`; if it is internal, substitute a plain `Dictionary<object, object?>` and record the correction |
| **The full required feature set** | Design lists four (`IConnectionUserFeature`, `IConnectionIdFeature`, `IConnectionItemsFeature`, `IConnectionHeartbeatFeature`). Determine empirically what .NET 10's `HubConnectionHandler` actually reads — expect additional lifetime/keep-alive/stateful-reconnect features. Record the *actual* list; it is a Phase 1 input. |

**Done when:** `findings/required-connection-features.md` lists every feature `HubConnectionHandler` touches, and whether each is mandatory or optional.

### B1 — Synthetic `ConnectionContext` + pipeline factory

`SwitchboardClientConnectionContext : ConnectionContext` with a `_toHub` / `_fromHub` `Pipe` pair, `Transport` = (reader `_toHub.Reader`, writer `_fromHub.Writer`), `ConnectionId` settable, a real `Abort()` and `ConnectionClosed` token, and the feature set from B0. `HubPipelineFactory` builds one `ConnectionDelegate` per hub type at startup and caches it.

A test-only service provider: `new ServiceCollection().AddLogging().AddSignalR()`, with `HubLifetimeManager<TestHub>` replaced by a **recording stub** that captures `SendAllAsync` / `SendConnectionAsync` / group / user calls.

### B2 — Drive a hub method with no client

Write a synthesized `HandshakeRequestMessage("json", 1)` into `_toHub.Writer`, then a JSON `Invocation` frame (`{"type":1,"invocationId":"1","target":"Echo","arguments":["hi"]}\x1e`), flush, and start the pipeline.

**Assert:** `TestHub.Echo` executed, with arguments bound correctly (record invocations on a shared sink the hub writes to). This is the "no client attached" proof.

### B3 — Identity flows

Set `IConnectionUserFeature` to a `ClaimsPrincipal` over `new ClaimsIdentity(claims, authenticationType: "Switchboard")` — **the non-null authentication type is load-bearing**; without it `IsAuthenticated` is false and every `[Authorize]` check fails.

**Assert:** `Context.User` is populated; `Context.UserIdentifier` equals the `NameIdentifier` synthesized from `userId` (this is what keeps `Clients.User(...)` consistent with the service's user index); a `[Authorize]`-decorated **hub method** is permitted with the principal and rejected (`Completion` with error) without it.

### B4 — Return-path split

From a single invocation that both returns a value *and* calls `Clients.All.SendAsync(...)`:

**Assert:** the `Completion` for the invocation appears on `_fromHub.Reader` (parse with `JsonHubProtocol`), while the `Clients.All` send appears on the **recording `HubLifetimeManager`** and *not* on the pipe. Two distinct outbound paths, per [04-design.md §11](../docs/docs/04-design.md) — conflating them is the failure mode this test exists to catch.

Also assert the outbound reader can identify and drop the synthetic handshake response (`{}`) and `PingMessage`, since the service owns the real handshake and client keep-alive.

### B5 — Lifecycle and rejection

- `OnConnectedAsync` runs when the pipeline starts.
- Completing `_toHub.Writer` (EOF) causes `OnDisconnectedAsync` to run and the pipeline task to complete.
- `RejectingHub.OnConnectedAsync` throwing produces a close frame with `allowReconnect: false` on `_fromHub` and a completed pipeline task **within a short timeout** — the test must fail on hang, not wait forever. Wrap every pipeline await in a bounded timeout (e.g. 5s) so a deadlock is a red test rather than a stuck CI job.

### B6 — Known-incompatibility confirmation

Assert `Context.GetHttpContext()` returns `null` (no `IHttpContextFeature`) and record it — it is already a documented incompatibility and a Phase 5 compatibility-matrix row; the point here is to confirm it fails *predictably* (null, not a crash inside the framework).

**There is no fallback for Workstream B.** If `HubConnectionHandler` cannot be driven this way, the Connector design must be rethought before Phase 1 — which is why it is proven here.

---

## 5. Sequencing and timebox

Time-boxed to **~5 working days**. A/B are independent after their recon steps and can be interleaved; A0 and B0 first, because they carry all the discovery risk.

| Day | Work | Gate |
|---|---|---|
| 1 | A0 + B0 recon; solution scaffolding; A1 control case | **Gate 1:** metadata marker + replacement API confirmed, or A6 fallback declared |
| 2 | A2, A3 | **Gate 2:** redirect returned *and* 401 preserved on `[Authorize]` |
| 3 | B1, B2, B3 | **Gate 3:** hub method executes from raw bytes with correct identity |
| 4 | A4, A5 (both clients), B4, B5, B6 | — |
| 5 | Findings write-up, doc updates, promotion boundary marked | **Gate 4:** milestone check green |

If Gate 1 or Gate 3 is missed, stop and escalate rather than spending the remaining days — a failure at either gate is a Phase 1 design input, and reaching it early is the whole point of the spike.

---

## 6. Deliverable ↔ plan mapping

Every checkbox in [06-project-plan.md § Phase 0](../docs/docs/06-project-plan.md):

| Deliverable | Task |
|---|---|
| Detect the negotiate endpoint | A0 |
| Take over the endpoint | A2 |
| Assert metadata is preserved | A3 |
| Prove the redirect end-to-end | A5 |
| Confirm ordering/isolation | A4 |
| Drive a hub method with no client | B2 |
| Confirm identity flows | B3 |
| Confirm the return path split | B4 |
| Confirm lifecycle + rejection | B5 |

---

## 7. Outputs

### Carried into Phase 1
- `Phase0.Spike.Connector/Negotiate/*` → `Keryhe.Switchboard.Connector` (stub redirect target swapped for the real proxy-forwarding call), **or** the A6 fallback if the spike took it.
- `Phase0.Spike.Connector/Dispatch/*` → the Connector's synthetic-connection layer, with the verified feature set.
- `Phase0.Spike.Tests` assertions → seed for the Phase 1 integration test.
- `findings/` — the verified API facts (exact metadata type, exact replacement API, exact required features). These are the reason the spike is worth running; write them down even where they simply confirm the design.

### Discarded
`Phase0.Spike.Host` (throwaway host, stub redirect target, hardcoded tokens/URLs, test hubs) — scaffolding only.

### Documentation updates required at the end
- `docs/docs/00-review-findings.md` — a "Phase 0 spike results" entry: what was confirmed, what was corrected, what surprised.
- `docs/docs/04-design.md §8` — **only if** the A6 fallback was taken (mechanism change), or if the real metadata marker differs from `NegotiateMetadata`.
- `docs/docs/04-design.md §11` — correct the feature table and any code sketch that did not survive contact (e.g. `ConnectionItems`).
- `docs/docs/06-project-plan.md` — tick the Phase 0 boxes; adjust the Phase 1 "promote the skeleton" item if the shape changed.

---

## 8. Risks specific to this spike

| Risk | Mitigation |
|---|---|
| `NegotiateMetadata` is internal or not attached in .NET 10 | A0 runs first and dumps real metadata; route-pattern matching and the A6 fallback are the escape hatches |
| Tests pass because the framework's negotiate delegate *also* ran | Assert on the **absence** of `connectionId`/`availableTransports`, not just the presence of `url` |
| `[Authorize]` test passes for the wrong reason (e.g. auth middleware rejecting before routing completes) | Pair the 401 test with the direct metadata assertion in A3 |
| Synthetic-connection tests hang instead of failing | Bounded timeout on every pipeline await; hang ⇒ red test |
| Spike code quietly becomes Phase 1 code without review | Physical separation under `spike/`; promotion is an explicit Phase 1 deliverable |
| Spike overruns its box chasing a framework detail | Gates 1–3 with a stop-and-escalate rule |

---

## 9. Definition of done

1. All nine deliverables in §6 are green as automated tests (`dotnet test spike/Phase0.Spike.sln`), plus the JS client check.
2. **Milestone check:** in the throwaway host — (a) an unmodified `@microsoft/signalr` client *and* a .NET `HubConnection` both negotiate against a `MapHub`-mapped route and get redirected to the stub target purely via the registered policy (or the fallback), with `[Authorize]` still enforced; and (b) a hub method runs to completion with correct identity, driven only by bytes written into a synthetic connection's pipe — no SignalR fork, no reflection into internals.
3. `findings/` is written and the doc updates in §7 are applied.
4. The promotion boundary is explicit: everything under `Phase0.Spike.Connector` compiles with no dependency on `Phase0.Spike.Host`.
