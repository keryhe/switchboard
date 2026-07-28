# Phase 3 — Scale-Out & Resilience: Implementation Plan

**Source of truth:** [06-project-plan.md § Phase 3](../docs/docs/06-project-plan.md), [04-design.md §§2–5, 7](../docs/docs/04-design.md), [ADR-002](../docs/docs/07-adr/ADR-002-connection-registry.md), [ADR-003](../docs/docs/07-adr/ADR-003-backplane.md), [02-architecture.md](../docs/docs/02-architecture.md), [05-data-models.md](../docs/docs/05-data-models.md), [00-review-findings.md](../docs/docs/00-review-findings.md).

**Goal.** Multiple service nodes and multiple app servers, no sticky sessions, fault-tolerant reconnection. Orleans replaces both the distributed registry and the backplane — no Redis.

**Milestone check.** A rolling restart of one service node does not drop client connections that reconnect within the reconnect window; a broadcast originating at an app server connected to Node A reaches a client connected to Node B; no Redis anywhere in the stack.

Phases 1 and 2 built a complete single-node service. Everything in it that touches routing was written against **node-local** state — deliberately, and it is called out as such in [Phase 2's "What Phase 3 inherits"](../docs/docs/06-project-plan.md). Phase 3's work is therefore not "add a backplane next to the existing fan-out"; it is **changing the shape of fan-out itself**, then substituting a distributed registry underneath it. The slice order below front-loads that reshaping so it lands while the single-node test suite is still the safety net, and only then introduces Orleans.

---

## 1. Preconditions — what Phases 1–2 already settled

| Established | Consequence for Phase 3 |
|---|---|
| `IConnectionRegistry` is async from day one (ADR-002) | The Orleans implementation is a substitution, not an interface change — but see **D14/D15**: two of its members and one of its model fields cannot survive the substitution as written |
| `IBackplane` exists and has been `NoOpBackplane` since Phase 1, untouched | Phase 3 is the first caller. No existing behavior depends on it, so **Slice 0 can wire the call sites before any implementation exists** |
| `ILocalTransportRegistry` already isolates transport handles from routing | Already the right shape. Phase 3 widens it from "transports" to "everything node-local", per the group-membership finding already resolved in [00-review-findings.md](../docs/docs/00-review-findings.md) |
| All fan-out goes through `DefaultMessageRouter.FanOutAsync` → `ILocalTransportRegistry` | One choke point to change. Group/user/broadcast all share it |
| `ClientConnectionLifecycle` is transport-agnostic and shared by all three transports | Reconnect (`Close{allowReconnect:true}`) is written once, not per transport |
| `ServerEnvelope` `[Key(0..11)]` is a wire contract; append only | Phase 3 needs **no new envelope fields** — everything it adds is service-internal (grain calls), not app-server-facing |
| `RoundRobinServerConnectionSelector` picks the least-loaded *local* server connection | **D18** widens "local" to "cluster-wide". The selector interface survives; its input set changes |
| Hub route names are a single path segment | Unchanged; Phase 3 adds no client-facing routes |

### New framework facts verified while writing this plan

Checked empirically against **Orleans 10.2.2** (a real two-silo cluster and a reflection pass over `Orleans.Core`/`Orleans.Runtime`), not assumed from the design docs. Each of these contradicts something a reasonable implementer would otherwise take from ADR-003 or [04-design.md §7](../docs/docs/04-design.md):

1. **Cross-silo observer delivery works exactly as ADR-003 describes.** Two silos in one cluster, a hub grain activated on one of them, each silo registering its own observer through **its own** `IGrainFactory`: a broadcast with `originNodeId: "node-a"` reached the observer object living in silo B and skipped A. This is the load-bearing claim of the whole phase and it holds.

2. **`IGrainFactory.CreateObjectReference<T>(IGrainObserver)` returns `T` synchronously.** It is not `ValueTask<T>` — that was the Orleans 3/7-era signature that most sample code online still shows. `DeleteObjectReference<T>` is likewise `void`.

3. **A dead node's observer reference throws `Orleans.Runtime.ClientNotAvailableException` — forever, on every subsequent call — and nothing evicts it.** Verified by stopping silo B without unsubscribing: every later broadcast failed for `node-b`, indefinitely. ADR-003's "grain cleans up stale references when observer calls fail" is only true if the grain is written to catch and evict; nothing in Orleans does it for you.

4. **…which means ADR-003's `void`-returning `IHubObserver` methods cannot work.** Observer methods *may* be `void` (verified: one-way, fire-and-forget) or `Task` (verified: awaited, failures surface at the caller). With `void` there is no exception for the grain to catch, so a dead node can never be detected or evicted. See **D16**.

5. **A grain deactivation silently drops every observer subscription.** Verified: subscribers `2` → deactivate → subscribers `0` on reactivation, with no error anywhere and no notification to the nodes that thought they were subscribed. Cross-node fan-out simply stops. ADR-003's "a node calls `SubscribeAsync` at startup and `UnsubscribeAsync` at shutdown" is therefore **not sufficient** — a re-subscribe heartbeat is mandatory, not an optimization.

6. **`ObserverManager<TIdentity, TObserver>` (in `Orleans.Utilities`) does not fit this use case**, despite looking like it was written for it. Two blockers, both verified: its `Notify` predicate is `Func<TObserver, bool>` — over the *observer*, not the identity — so "skip the origin node id" is not expressible through it; and `Notify` swallows failures without evicting (count stayed at 2 after notifying a dead observer). Its expiry model (`ClearExpired()` + `Subscribe` refreshing a timestamp) is still the right *idea* — **D16** reuses the idea, not the type.

7. **`AddMemoryGrainStorage` ships in `Microsoft.Orleans.Server`.** The separate `Microsoft.Orleans.Persistence.Memory` package listed in [06-project-plan.md's package table](../docs/docs/06-project-plan.md) is not needed for dev/single-node storage — one fewer dependency than the roadmap assumes.

8. **The AdoNet packages contain no SQL scripts.** `Microsoft.Orleans.Clustering.AdoNet` and `Microsoft.Orleans.Persistence.AdoNet` 10.2.2 ship `lib/` and a README only. The schema must be vendored from the Orleans repository and applied by the operator — the deliverable is "vendor and document", not "write from scratch", and definitely not "the package creates its tables".

9. **Orleans 10.2.2 restores audit-clean and coexists with this repo's dependency set.** Verified by adding `Microsoft.Orleans.Server` + both AdoNet providers to the real `Keryhe.Switchboard.Server` project and building the full solution: `Build succeeded. 0 Warning(s). 0 Error(s)` — no conflict with `MessagePack` 3.1.8, the SignalR MessagePack protocol package, or `System.IdentityModel.Tokens.Jwt` 8.2.1, and no NU19xx advisories. (Change reverted; it belongs in Slice 1.) `Microsoft.Orleans.TestingHost` 10.2.2 also exists, though this plan does not depend on it — see §5.

10. **`ClientConnectionState.TransportHandle` is written twice and never read.** `grep` across `src/` and `tests/`: set in `ClientConnectionLifecycle` and in one test fixture, read nowhere — every actual transport lookup already goes through `ILocalTransportRegistry`. It is the one field that makes the state object impossible to put in grain storage, and removing it costs nothing. See **D15**.

11. **Every connection is recorded as `TransportType.WebSockets`, whatever transport it actually used.** All three transports share `ClientConnectionLifecycle.RunAsync`, which hardcodes `Transport = TransportType.WebSockets` ([ClientConnectionLifecycle.cs:82](../src/Keryhe.Switchboard.Server/ClientConnections/ClientConnectionLifecycle.cs:82)) — verified: `ClientConnectionEndpoint`, `SseClientEndpoint`, and `LongPollingClientEndpoint` all call it. Harmless today because nothing reads the field, which is exactly why it survived Phase 2. It stops being harmless the moment Phase 4 emits `signalr.client_connections.active` *by transport* or the management API reports a connection's transport. Phase 3 rewrites this constructor anyway (**D15**), so fix it there rather than leaving a wrong value to be persisted into grain state.

12. **`ServerConnectionId` has exactly two lookup sites**, both `hubRegistry.GetHub(...).ServerConnections.GetValueOrDefault(state.ServerConnectionId)` — [DefaultMessageRouter.cs:35](../src/Keryhe.Switchboard.Server/Routing/DefaultMessageRouter.cs:35) and [RoutingServerEnvelopeDispatcher.cs:86](../src/Keryhe.Switchboard.Server/ServerConnections/RoutingServerEnvelopeDispatcher.cs:86). Both index a node-local dictionary by the bare id, so both break silently — a miss, not an exception — the moment **D18** makes the value node-qualified.

---

## 2. Decisions

Seven decisions, **D14–D20**, continuing D1–D6 (Phase 1) and D7–D13 (Phase 2) so a code comment saying "plan decision D7" stays unambiguous. Each is ready to implement as written; revisit only if implementation contradicts one.

### D14 — Fan-out is "local index, then one backplane publish" — the registry is never enumerated on the hot path

Today `BroadcastAsync` enumerates `IConnectionRegistry.GetAllAsync(hubName)` and writes to whichever targets happen to have a local transport. Substituting the Orleans registry underneath that shape would make **every broadcast a grain call returning every connection in the cluster**, most of which the calling node cannot write to anyway. That is the single worst thing this phase could do to the hot path.

**Recommendation: invert it.** A node fans out to *its own* connections from a node-local index, then publishes the message once to `IBackplane`; each receiving node does the same locally. This is exactly the flow [ADR-003](../docs/docs/07-adr/ADR-003-backplane.md) and [02-architecture.md § Scale-Out Routing](../docs/docs/02-architecture.md) already draw — it just is not what the code does yet.

Concretely, `ILocalTransportRegistry` grows from `connectionId → IClientTransport` into the node's whole local view: hub membership, group membership, user index, and the connection's negotiated `hubProtocol` (needed for **D7** payload selection at fan-out time). This is the "node-local cache beside the transport handles" that [00-review-findings.md](../docs/docs/00-review-findings.md) already resolved for group membership, generalized.

The registry (in-memory or Orleans) keeps `RegisterAsync`/`AddToGroupAsync`/`GetAsync` and remains the source of truth for **ownership lookups, disconnect cleanup, and the Phase 4 management API** — none of which are per-message. `GetAllAsync` / `GetGroupMembersAsync` / `GetUserConnectionsAsync` stay on the interface but are **no longer called by the router**; their remaining callers are diagnostics and tests. Mark them as such in XML docs, or a future implementer will wire them back into fan-out and quietly reintroduce the cluster-wide scan.

Do this refactor in Slice 0, **before Orleans exists**, with the single-node suite as the net. `NoOpBackplane` makes it behavior-preserving by construction.

### D15 — `ClientConnectionState` must be serializable; `TransportHandle` goes

A grain cannot hold an `IClientTransport`. Finding 10 says the field is already dead, so this is a deletion, not a redesign: drop `TransportHandle` from `ClientConnectionState`, and let every consumer resolve transports through `ILocalTransportRegistry` (which is what they all already do).

While there: `Groups` on the state object is a `ConcurrentDictionary` used as a set, which is fine in memory and wrong in grain state. Group membership is owned by `IGroupGrain` under clustering and by the node-local index for fan-out; the copy on `ClientConnectionState` exists only to make disconnect cleanup cheap. Keep it, but treat it as **derived** — the Orleans registry populates it on read from the connection grain's own record and never treats it as authoritative.

Two consequences of touching this constructor, both cheap and both worth doing here rather than later:

- **Fix `Transport`** (finding 11). The value is currently a lie for SSE and Long Polling, and this phase is what makes it durable — a wrong value written into grain storage outlives the process that wrote it. Pass the real `TransportType` in from each endpoint.
- **The Slice 0 gate needs precise wording.** Removing a `required` member edits every construction site, including [DefaultMessageRouterTests.cs:78](../tests/Keryhe.Switchboard.UnitTests/Routing/DefaultMessageRouterTests.cs:78). That is a compile-driven edit, not a semantic one, so the gate is: **no assertion may change**; construction sites may. Phase 2's Slice 4 rule ("any test that has to change is evidence the refactor changed semantics") is the right instinct but too blunt to apply verbatim to a field deletion.

`ServerConnectionId` becomes `{nodeId}:{serverConnectionId}` under **D18**; keep it a single opaque string so no signature changes — but note it is *parsed* at the two sites in finding 12, and a composite value silently misses a bare-keyed dictionary rather than throwing. Introduce a `ServerConnectionRef` parse/format helper in Slice 0 so there is one place that knows the format, and neither call site does string surgery inline.

### D16 — Observer methods return `Task`; the grain evicts on failure; every node re-subscribes on a heartbeat

Findings 3–5 make this unavoidable, and they contradict [04-design.md §7](../docs/docs/04-design.md)'s `IHubObserver` sketch (which declares `void` methods) — that sketch is a **doc correction due at the end of this phase**.

**Recommendation:**

```csharp
public interface IHubObserver : IGrainObserver
{
    Task OnBroadcast(byte[] payload, IReadOnlyDictionary<string, byte[]>? payloadsByProtocol, string[] excludedConnectionIds);
    Task OnGroupMessage(string groupName, byte[] payload, IReadOnlyDictionary<string, byte[]>? payloadsByProtocol, string[] excludedConnectionIds);
    Task OnUserMessage(string userId, byte[] payload, IReadOnlyDictionary<string, byte[]>? payloadsByProtocol);
    Task OnConnectionMessage(string connectionId, byte[] payload, string hubProtocol);
    Task OnClientMessage(string connectionId, byte[] payload, string hubProtocol);          // D18
    Task OnCloseConnection(string connectionId, string? error, bool allowReconnect);        // D18
}
```

- `Task`, not `void`, purely so failures are observable (finding 4).
- The observer body itself must never await client I/O — it writes to the bounded per-connection channels (`DropWrite`) exactly as local fan-out does, and returns. The `Task` is for *failure signalling*, not for delivery confirmation; ADR-003's "fire-and-forget, may be lost under partition" semantics are unchanged.
- The hub grain holds `Dictionary<string nodeId, (IHubObserver Observer, DateTimeOffset LastSeen)>`, iterates it with a **per-observer try/catch**, and evicts a node on `ClientNotAvailableException` — one dead node must not stop delivery to the others (verified failure mode, finding 3).
- Every node re-subscribes on a `SubscribeAsync` heartbeat (recommend `ObserverHeartbeatInterval`, default 30s, with eviction after 3 missed intervals). This is what makes finding 5 survivable — and it also keeps the hub grain activated, which is the same problem viewed from the other side.
- The payload carries **`payloadsByProtocol`** (Phase 2 **D7**), not a single encoding: the origin node cannot know the hub protocols negotiated by clients on other nodes any more than the Connector could know them across app-server instances. Same rule as D7 — a target whose protocol has no entry is skipped with one warning, never sent the wrong bytes.

Do not use `ObserverManager<,>` (finding 6).

### D17 — Cross-node group and user sends carry the *name*, not the member set

An observer call could carry either "here is the group name, work out who that is locally" or "here are the connection ids on your node". The first is what [04-design.md §7](../docs/docs/04-design.md)'s `OnGroupMessage(string groupName, …)` signature already implies, and it is the right choice: the receiving node's local index is authoritative for who it can actually write to, it needs no membership read on the send path, and it cannot go stale between the grain read and the write.

**Recommendation: publish by name.** `IGroupGrain`/`IUserGrain` are consulted for management queries, disconnect cleanup, and cross-checking — never on the fan-out path.

Accepted tradeoff, stated so it is not rediscovered as a bug: a group whose members all live on one node still costs one observer call per node in the cluster. At the node counts this design targets (single digits) that is cheaper than a membership round trip, and the optimization — ask `IGroupGrain` for the distinct node set and call only those observers — is a drop-in change later, with Phase 5 numbers to justify it.

### D18 — Server connections are assigned cluster-wide and stick to the connection

This is the deliverable the roadmap states in one line ("Multiple app server connections per hub (Pool): selector chooses least-loaded connection") and it hides the phase's second-hardest problem.

App servers connect *through the load balancer*, so their `ServerConnectionsPerHub` sockets land on arbitrary nodes. With 5 connections and 3 nodes, **a node can legitimately end up with zero server connections for a hub while the cluster is perfectly healthy.** Today that node would 503 every negotiate ([`DefaultNegotiationService`](../src/Keryhe.Switchboard.Server/Negotiate/DefaultNegotiationService.cs:34) checks the *node-local* `IHubRegistry`) and could not route a single `client_message`.

Rejected alternative: require the Connector to open a pool *per service node*. It makes every node individually addressable from every app server, which trades away the "everything talks to one LB address" deployment story that is the point of the phase, and needs a node-discovery mechanism that does not exist.

**Recommendation: assign once, at accept time, cluster-wide, and make the assignment sticky.**

1. `IHubGrain` tracks server connections as `{nodeId, serverConnectionId, logicalCount}` — registered/unregistered by `ServerConnectionEndpoint` on the node that physically holds the socket. **The grain owns the count, not the node.** `ServerConnectionState.Increment/DecrementLogicalCount` is a node-local counter today; under clustering the assigning grain is the only component that sees every assignment, so it increments on `AssignServerConnectionAsync` and decrements when the connection is released (client disconnect, or `close_connection` from the app server). The node-local counter stays for local diagnostics but must never be the input to a cluster-wide "least-loaded" decision — a node can only see its own share, which is precisely the bias the assignment exists to avoid.
2. At accept, the node calls `IHubGrain.AssignServerConnectionAsync(connectionId)` → `{nodeId, serverConnectionId}` (least-loaded across the cluster). It is recorded on the connection and never changes for that connection's life.
3. Stickiness is not a nicety: `open_connection` / `client_message` / `close_connection` are a **stateful sequence** against one synthetic connection inside one app server process ([04-design.md §11](../docs/docs/04-design.md)). Splitting a connection's messages across app servers would dispatch hub calls into a pipeline that never saw `open_connection`.
4. If the assigned node is this node (the common case), write to the local socket — no grain call, no observer hop. Otherwise send via `IHubObserver.OnClientMessage` to the owning node, which writes it to its own socket. The **reply path needs nothing new**: the app server answers with `send_to_connection`, that lands on the assigned node, and the existing owner-lookup path (**D17**'s sibling, `OnConnectionMessage`) delivers it to whichever node holds the client.
5. Negotiate's fail-fast becomes cluster-wide: `IHubGrain.HasActiveServerConnectionAsync(hubName)` instead of the node-local registry. Same 503, correct scope.
6. When a server connection drops, the hub grain knows every `connectionId` assigned to it — including ones on other nodes — and fans out `OnCloseConnection(connectionId, error, allowReconnect: true)` so those clients reconnect through negotiate. This is the roadmap's "client reconnect support" deliverable, and it is only implementable once assignment is grain-owned.

### D19 — The `connectionToken` cannot encode the owning node — the owner is not known when it is minted

[03-protocol.md §1.1](../docs/docs/03-protocol.md), [04-design.md §2](../docs/docs/04-design.md), and [05-data-models.md](../docs/docs/05-data-models.md) all state that in Phase 3 `connectionToken` encodes the owning node so a transport request landing elsewhere can be routed. Writing this plan exposed that it cannot: the token is minted at **step-2 negotiate**, and the owning node is whichever node the client's *subsequent* transport request happens to reach. Pre-assigning the owner at negotiate would force a permanent proxy hop onto every WebSocket connection whose upgrade landed on a different node — strictly worse than the problem it solves. **This is a doc correction, in the same category as the two Phase 0 found.**

Two real problems remain, and both are solved by grain lookups instead:

- **The pending-connection store is node-local.** Step 2 mints a `connectionToken` into `IPendingConnectionStore` on node X; the transport upgrade may land on node Y, which has never heard of it, and returns 401. This breaks *every* transport in a cluster, not just SSE/Long Polling — it is the first thing a two-node test will hit. **Recommendation:** a TTL'd `IPendingConnectionGrain` keyed by `connectionToken` under clustering, behind the existing `IPendingConnectionStore` interface, with the same `ClientTokenExpiry` TTL and one-shot consumption semantics. One grain call per connection *establishment* — not per message. **This lands in Slice 1, with the other state substitutions — not here.** It is a registry concern, it is not specific to SSE or Long Polling, and Slice 2's two-node tests cannot negotiate on one node and connect on the other without it.
- **SSE and Long Polling span many requests.** The establishing GET fixes the owner; later `POST` (send), `GET` (poll), and `DELETE` (close) may land anywhere. **Recommendation:** resolve `connectionToken → owning nodeId` (grain call, then cached in a node-local map so it costs nothing after the first miss) and **forward the HTTP request to the owning node** over its internal cluster address, which each node publishes into a node-registry grain at startup. WebSocket needs none of this — one socket, one node, by construction.

Two constraints on the forward hop, because the obvious implementation of each is wrong:

- **Only the transport endpoints (`GET`/`POST`/`DELETE /{hub}`) ever forward. Negotiate never does** — once the pending store is a grain, any node can answer a negotiate in full. This is not merely an optimization: forwarding negotiate would make the *peer address* node A sees be node B, and in any real deployment the nodes are inside `TrustedProxyNetworks`, so **D11**'s Pattern A allowlist would silently degrade into "trusted from anywhere" — a peer that must not be believed becomes one that is, via a hop it never asked for. If a future change does need to forward negotiate, the identity headers must be stripped at the forwarding node.
- **Forwarding grants no authority.** The forwarded request carries the client's original `access_token`, and the receiving node validates it exactly as it would a direct request. No new token type, no node-to-node trust relationship, nothing for ADR-004's three-token model to absorb. Add a single-hop marker header and refuse to forward a request that already carries it, so a stale owner cache cannot produce a forwarding loop between two nodes.

If forwarding proves messier than it looks, the documented fallback is to require session affinity **for SSE and Long Polling only** (WebSocket remains affinity-free) — but that concedes part of the phase's "no sticky sessions" goal, so it is a fallback, not a first choice, and it must be recorded in `00-review-findings.md` if taken.

### D20 — One registry contract, two implementations, one conformance suite

`InMemoryConnectionRegistry` does not go away — it stays the default for single-node deployments (ADR-002), and `UseOrleansCluster` (already in `SwitchboardOptions`) flips the DI registration.

**Recommendation:** extract the existing registry tests into an implementation-agnostic conformance suite (an abstract xunit base or a theory over a factory) and run it against both implementations. Two implementations of one interface with two different test suites is how they drift; the drift will be in exactly the edge cases (unregister with stale group membership, duplicate register, protocol set before/after group join) that only bite in production.

**`IConnectionRegistry` is not the only interface being doubled.** Phase 3 also gains an Orleans implementation of `IHubRegistry` (server-connection inventory, **D18**) and of `IPendingConnectionStore` (**D19**), and each has the same drift exposure — `IPendingConnectionStore` more than the others, since its one-shot-consumption and TTL-expiry semantics are the entire security value of `connectionToken` and are invisible in a happy-path test. All three get the conformance treatment.

Same discipline for state: Orleans grain state types use `[GenerateSerializer]` + `[Id(n)]`, which is **a second append-only wire contract alongside `ServerEnvelope`'s `[Key(n)]`** — it is persisted in SQL and read back by a different build after a rolling upgrade. Never reorder or reuse an `[Id]`. Grain interfaces and their methods get `[Alias("…")]` so a rename is not a breaking change. Pin both with a test in the same spirit as the existing `ServerEnvelopeSerializer` key-order test.

---

## 3. Target layout

One new project — `Keryhe.Switchboard.Orleans` — created only now that it has contents (the roadmap's "don't scaffold empty placeholder projects" guidance). `Keryhe.Switchboard.Management` stays uncreated until Phase 4.

```
src/Keryhe.Switchboard.Orleans/                      # new — Core + Microsoft.Orleans.Server
  Grains/IHubGrain.cs, IGroupGrain.cs, IUserGrain.cs, IConnectionGrain.cs,
         IPendingConnectionGrain.cs, INodeRegistryGrain.cs                # D18, D19
  Grains/HubGrain.cs, GroupGrain.cs, UserGrain.cs, ConnectionGrain.cs, …
  Observers/IHubObserver.cs, HubObserverImpl.cs, ObserverHeartbeatService.cs   # D16
  OrleansConnectionRegistry.cs, OrleansHubRegistry.cs, OrleansPendingConnectionStore.cs
  OrleansObserverBackplane.cs
  SwitchboardOrleansExtensions.cs                    # silo co-hosting + DI substitution
  Sql/{SqlServer,PostgreSQL,MySQL}/*.sql             # vendored, not authored (finding 8)

src/Keryhe.Switchboard.Core/
  ILocalTransportRegistry.cs                         # D14 — widened to the node's whole local view
  Models/ClientConnectionState.cs                    # D15 — TransportHandle removed
  Models/SwitchboardOptions.cs                       # + NodeId, ObserverHeartbeatInterval, InternalUrl

src/Keryhe.Switchboard.Server/
  Routing/DefaultMessageRouter.cs                    # D14 — local fan-out + one backplane publish
  ClientConnections/…                                # D19 — owner resolution + forward hop
  Program.cs                                         # UseOrleansCluster branch, cluster-aware /healthz

src/Keryhe.Switchboard.Registry/
  LocalTransportRegistry.cs                          # D14 — node-local indexes live here
```

**Package changes:** `Microsoft.Orleans.Server`, `Microsoft.Orleans.Clustering.AdoNet`, `Microsoft.Orleans.Persistence.AdoNet` — all **10.2.2** (verified audit-clean and conflict-free, finding 9) — in `Keryhe.Switchboard.Orleans`, with `Keryhe.Switchboard.Server` taking a project reference. **Not** `Microsoft.Orleans.Persistence.Memory` (finding 7). The Orleans code generator runs off `Microsoft.Orleans.Sdk`, which `Server` brings transitively.

---

## 4. Slices

Each slice ends runnable and independently testable. Ordering is deliberate: **reshape fan-out before introducing Orleans**, **registry before backplane**, and **two silos as early as the mechanism allows**, because every interesting failure in this phase is a two-node failure.

### Slice 0 — Fan-out inversion and state cleanup (no Orleans, no behavior change)

- `ILocalTransportRegistry` widened per **D14**: hub / group / user indexes and the connection's `hubProtocol`, maintained alongside transport registration.
- `DefaultMessageRouter` fans out from the local index, then calls `IBackplane.Publish*Async` exactly once per operation, passing this node's id. `NoOpBackplane` keeps behavior identical.
- **All four** publish call sites land here, including `PublishToConnectionAsync` on a local miss in `RouteToConnectionAsync` — today that path logs "may have disconnected or live on another node" and drops, which is the exact branch that becomes a backplane call in Slice 3. Wiring it now means Slice 3 implements a backplane; wiring it later means Slice 3 also re-edits the router.
- `IBackplane` signatures updated to carry `payloadsByProtocol` and `originNodeId` (**D16**) — it has no implementations yet, so this is free now and expensive later.
- `ClientConnectionState.TransportHandle` deleted (**D15**, finding 10); `Transport` now carries the real transport (finding 11); `ServerConnectionRef` parse/format helper introduced ahead of **D18** (finding 12).
- Conformance suites extracted (**D20**) for `IConnectionRegistry`, `IHubRegistry`, and `IPendingConnectionStore`, running against the in-memory implementations.
- `SwitchboardOptions.NodeId` (GUID per process, overridable) and `InternalUrl`.

**Gate:** all 101 unit + 1 integration tests pass with **no assertion changes**; construction-site edits forced by the removed `required` member are expected and permitted (**D15**). Otherwise this slice is behavior-preserving by definition, as Phase 2's Slice 4 was. Plus: a recording fake backplane proves each broadcast / group / user / targeted-miss send publishes exactly once, with the right origin node id, *after* local delivery. A new assertion pins finding 11 — an SSE connection and a Long Polling connection are recorded with their own `TransportType`, not `WebSockets`.

### Slice 1 — `Keryhe.Switchboard.Orleans`: grains, registry, one silo

- Grain interfaces + state types with `[Alias]` / `[GenerateSerializer]` / `[Id(n)]` (**D20**).
- `HubGrain`, `GroupGrain`, `UserGrain`, `ConnectionGrain`; `OrleansConnectionRegistry` delegating via `IGrainFactory`.
- **`IPendingConnectionGrain` + `OrleansPendingConnectionStore`** (**D19**), moved forward from Slice 5: it is a state substitution like the others, and without it no two-node test can negotiate on one node and connect on another — which every subsequent slice's tests need to do.
- Silo co-hosted in the service host behind `UseOrleansCluster`, memory clustering + `AddMemoryGrainStorage` for dev (finding 7); `UseOrleansCluster = false` keeps the Phase 2 wiring untouched.
- No backplane and no observers yet — a single clustered node that behaves exactly like the in-memory one.

**Gate:** the conformance suites pass against `OrleansConnectionRegistry` and `OrleansPendingConnectionStore` — including the one-shot-consumption and TTL-expiry cases, which are the security-bearing ones. The **entire existing end-to-end suite** passes a second time with `UseOrleansCluster = true` on one node — same tests, different registry, which is the substitution ADR-002 promised. A byte-pinned test asserts grain-state `[Id(n)]` ordering.

### Slice 2 — Observer backplane, two silos

- `IHubObserver` per **D16** (`Task`-returning), `HubObserverImpl` (plain class, holds `ILocalTransportRegistry`), `OrleansObserverBackplane`.
- Hub grain: subscribe/unsubscribe, origin-node skip, per-observer try/catch with eviction on `ClientNotAvailableException`, `lastSeen` tracking.
- `ObserverHeartbeatService`: re-subscribe every `ObserverHeartbeatInterval` (**D16**, finding 5).
- Broadcast only — group/user/targeted land in Slice 3.

**Gate:** two silos in one test process (`UseLocalhostClustering(siloPort, gatewayPort, primarySiloEndpoint)` — verified working in this exact configuration); a client on node B receives a broadcast from an app server connected to node A. Negotiate and the transport request are deliberately aimed at **different** nodes from this slice onward — Slice 1's pending-connection grain is what makes that legal, and pinning it here means no later slice can regress it unnoticed. Kill node B without unsubscribing: node A's next broadcast still delivers to survivors, node B is evicted after its first failure, and a **second** broadcast does not retry it (finding 3 is the regression this pins). Deactivate the hub grain: within one heartbeat every node is re-subscribed and delivery resumes (finding 5).

### Slice 3 — Cross-node targeted, group, and user sends

- `send_to_connection`: local fast path first (`ILocalTransportRegistry` hit → done, no grain call), otherwise `IConnectionGrain.GetOwnerNodeAsync` → `OnConnectionMessage` on that node's observer.
- Group and user sends published **by name** per **D17**; each receiving node resolves against its own index and applies `excludedConnectionIds` and **D7** protocol selection locally.
- Disconnect cleanup across grains (connection, hub, groups, user index) using the node-local group cache to avoid a cross-grain scan.

**Gate:** two nodes, a group with members on both — one `Clients.Group(...)` send reaches both, an excluded connection on the *remote* node is still excluded, and a mixed-protocol pair (JSON on node A, MessagePack on node B) each receive correct bytes. `Clients.User(...)` reaches a user with connections on both nodes. A message targeting a connection that has since disconnected produces one warning and no write.

### Slice 4 — Cluster-wide server-connection pool and reconnect (**D18**)

- Hub grain owns the server-connection inventory, the per-connection assignment counts (**D18**), and `AssignServerConnectionAsync` (least-loaded cluster-wide); `IServerConnectionSelector` survives with a widened input set.
- `OrleansHubRegistry` behind `IHubRegistry`, passing Slice 0's conformance suite.
- `ServerConnectionId` becomes node-qualified via the Slice 0 `ServerConnectionRef` helper; both lookup sites from finding 12 updated together.
- `client_message` to a non-local assigned node via `OnClientMessage`; local writes stay local.
- Negotiate's 503 becomes cluster-wide.
- Server-connection loss → `OnCloseConnection(..., allowReconnect: true)` to every assigned client wherever it lives; `ClientFrameWriter.Close(protocol, error, allowReconnect: true)` already supports the flag.

**Gate:** a node holding **zero** server connections for a hub still completes the full flow — negotiate, connect, invoke a hub method, receive the completion. Assignment is proven sticky across many messages. Killing an app server's pool sends `Close{allowReconnect:true}` to clients on both nodes, and a real `HubConnection` with automatic reconnect re-establishes through negotiate.

### Slice 5 — Node affinity for SSE and Long Polling (**D19**)

- `INodeRegistryGrain`: each node publishes its `InternalUrl` at startup, removes it at shutdown.
- Owner resolution + internal forward hop for `POST`/`GET`(poll)/`DELETE` landing on a non-owner, with the resolved owner cached node-locally.
- Single-hop marker header and the refusal to forward an already-forwarded request (**D19**).
- (`IPendingConnectionGrain` is **not** here — it moved to Slice 1.)

**Gate:** with requests deliberately round-robined across two nodes (no affinity anywhere), a .NET client pinned to `ServerSentEvents` and one pinned to `LongPolling` each complete the full flow, including a group message and a server push. A poll forwarded to the owner returns the same bytes a direct poll would. The Phase 2 reaper still closes an abandoned connection within `DisconnectTimeout` when the polls were arriving via forwards. A request carrying the marker header for a connection this node does not own is rejected rather than forwarded again — assert the absence of the second hop, not just the presence of the first.

### Slice 6 — ADO.NET providers, schema, and configuration

- Vendored Orleans SQL scripts for SQL Server, PostgreSQL, and MySQL (finding 8 — they are **not** in the packages), split clustering vs. persistence, with a README stating which to run in which order and that the service does not create them.
- Provider selection from `SwitchboardOptions` (`OrleansAdoNetConnectionString`, `OrleansAdoNetInvariant`, `OrleansClusterId`, `OrleansServiceId` — all already declared); startup validation refusing to boot with `UseOrleansCluster = true` and no connection string, matching the existing fail-fast posture for `PublicUrl` and Pattern A.

**Gate:** two nodes clustering through a real database (container or local instance) complete Slice 2's and Slice 3's scenarios. If no database is available in the environment where this lands, that is called out explicitly as untested rather than assumed working — an unverified persistence provider is exactly the thing that fails first in production.

### Slice 7 — Rolling restart, readiness, and the milestone

- `/healthz`: 200 only when the silo is active **and** the cluster has at least one server connection for every registered hub — the readiness gate the risk register calls for so a load balancer stops routing to a node whose silo is still starting. **Answer it from a short-lived cached value (1–2s), not a grain call per probe**: load balancers probe every node every couple of seconds, and a liveness endpoint that does cluster I/O fails exactly when the cluster is unwell — which is when the probe most needs to answer.
- Graceful shutdown: unsubscribe observers, deregister from the node registry, drain.
- Out-of-process two-node integration test (`ProcessFixture`, extended to spawn two `Keryhe.Switchboard.Server` processes plus `SampleChatApp.Api`).

**What "the reconnect window" means here**, since the roadmap's phrasing implies a server-side timer that does not exist and must not be built: stateful reconnect is a [standing non-goal](../docs/docs/01-overview.md), so a reconnect is an ordinary fresh negotiate — no buffered messages, no resumed connection, no session to expire. The window is entirely the *client's* own retry policy. The milestone therefore asserts that a client whose node went away re-establishes through negotiate against a surviving node and resumes receiving, not that any state survived the restart.

**Gate:** the Phase 3 milestone — restart node A while a client connected to node A is live; it reconnects (its own retry policy, against a surviving node) and continues receiving group messages; a client on node B is unaffected throughout; broadcasts keep flowing across nodes for the duration. `grep -ri redis` over the solution returns nothing.

---

## 5. Testing strategy

Phase 2's discipline carries forward (real clients as ground truth, assert absence, bound every wait). Four additions specific to this phase:

- **Two nodes is the default, not the exotic case.** Every routing test written from Slice 2 onward runs against two silos. A single-node pass proves almost nothing here — the entire phase exists to fix behavior that only differs when there are two.
- **Test the failure paths, not just the topology.** The three verified failure modes — dead observer (finding 3), grain deactivation (finding 5), a node with no server connection (**D18**) — each get a dedicated test that *induces* the failure. They are all silent in production; none produces an error a passing happy-path test would notice.
- **Two silos in one process is enough for most of it.** Verified working with `UseLocalhostClustering(siloPort, gatewayPort, primarySiloEndpoint)` at distinct ports; `Microsoft.Orleans.TestingHost` is available if a richer `TestCluster` is wanted, but the plain two-host setup already proved cross-silo observer delivery and needs no extra dependency. The *milestone* still runs out-of-process, for the same reason Phases 1–2 did: only real Kestrel processes exercise real sockets, real restarts, and real reconnect.
- **Run the whole suite twice.** Once with `UseOrleansCluster = false` and once with `true`. That parameterization is what keeps the in-memory path — still the recommended single-node deployment — from rotting while all the attention is on the clustered one.

---

## 6. Deliverable ↔ slice mapping

Every checkbox in [06-project-plan.md § Phase 3](../docs/docs/06-project-plan.md):

| Deliverable | Slice |
|---|---|
| `Keryhe.Switchboard.Orleans` project: grain + observer interfaces, `[GenerateSerializer]` | 1, 2 |
| Grain implementations: state, registration, observer fan-out skipping `originNodeId` | 1, 2 |
| `HubObserverImpl` (plain class, per silo, uses `ILocalTransportRegistry`) | 2 |
| `ILocalTransportRegistry` (node-local, never in grain state) | 0 (widened), 2 |
| `OrleansConnectionRegistry` | 1 |
| `OrleansObserverBackplane` | 2, 3 |
| Node ID generation, passed as `originNodeId` | 0 (option + plumbing), 2 |
| Silo co-hosting; memory providers for dev, ADO.NET for production | 1 (memory), 6 (ADO.NET) |
| ADO.NET schema scripts (SQL Server + PostgreSQL + MySQL) | 6 |
| Multiple app server connections per hub; least-loaded selector | 4 |
| Server connection pool management; remove from hub grain on disconnect | 4 |
| Client reconnect: `Close{allowReconnect:true}` on server-connection loss | 4 |
| `/healthz` gated on silo active + a server connection per hub | 7 |
| Integration tests: two nodes, two app servers, cross-node broadcast | 2 (mechanism), 7 (milestone) |
| *(not a roadmap item)* cross-node pending-connection store — prerequisite for all of the above | 1 |

Not on the roadmap's list but required by it: the fan-out inversion (**D14**, Slice 0) and the `ClientConnectionState` cleanup (**D15**) are enabling work; the cross-node pending-connection store (**D19**, Slice 1) and SSE/Long-Polling owner forwarding (**D19**, Slice 5) are a gap the roadmap does not anticipate — they fall out of "no sticky sessions" meeting Phase 2's multi-request transports, and without the first of them a clustered deployment 401s on connect, on every transport.

Two pre-existing defects are folded into Slice 0 rather than filed separately, because this phase rewrites the exact code that carries them and would otherwise make them durable: every connection being recorded as `TransportType.WebSockets` (finding 11), which Phase 4's per-transport metrics depend on, and the two bare-keyed `ServerConnectionId` lookups (finding 12) that **D18** turns into silent misses.

---

## 7. Documentation updates due at the end of Phase 3

- **[04-design.md §7](../docs/docs/04-design.md)** — `IHubObserver` methods return `Task`, not `void`, and why (findings 3–5); the eviction and heartbeat requirements; `payloadsByProtocol` on the observer calls.
- **[ADR-003](../docs/docs/07-adr/ADR-003-backplane.md)** — same correction to the observer sketch; replace "grain cleans up stale references when observer calls fail" with what the grain must actually do; record that `ObserverManager<,>` was evaluated and rejected (finding 6).
- **[04-design.md §2](../docs/docs/04-design.md), [03-protocol.md §1.1](../docs/docs/03-protocol.md), [05-data-models.md](../docs/docs/05-data-models.md)** — remove "`connectionToken` encodes the owning node" from all three; describe the grain-lookup + forward-hop design instead (**D19**).
- **[04-design.md §5](../docs/docs/04-design.md)** — fan-out is local-index + one backplane publish; the registry is not enumerated per message (**D14**).
- **[04-design.md §3](../docs/docs/04-design.md)** — cluster-wide server-connection assignment, stickiness and why it is mandatory, and the zero-local-server-connections case (**D18**).
- **[04-design.md §1](../docs/docs/04-design.md)** — negotiate's 503 is now a cluster-wide check; the pending-connection store is a grain under clustering.
- **[05-data-models.md](../docs/docs/05-data-models.md)** — `ClientConnectionState` without `TransportHandle`; `ServerConnectionId` is node-qualified and has a parse helper; new `SwitchboardOptions` fields; grain state types and the `[Id(n)]` append-only rule.
- **[ADR-002](../docs/docs/07-adr/ADR-002-connection-registry.md)** — its consequence "the in-memory implementation must not store `IClientTransport` references in any way that could be confused with distributed state" is now enforced by the type rather than by convention (**D15**); say so, since it is the rare case where an ADR consequence became structural.
- **[03-protocol.md §§1.5–1.6](../docs/docs/03-protocol.md)** — in a clustered deployment an SSE/Long Polling `POST`/`GET`/`DELETE` may be served by a node other than the one it was addressed to, transparently to the client; and the single-hop rule.
- **[03-protocol.md § Health Check](../docs/docs/03-protocol.md)** — the clustered readiness criterion, and that the public probe answers from a cached value rather than doing cluster I/O.
- **[06-project-plan.md](../docs/docs/06-project-plan.md)** — tick Phase 3; drop `Microsoft.Orleans.Persistence.Memory` from the package table (finding 7); pin Orleans at 10.2.2; note what Phase 4 inherits.
- **[02-architecture.md](../docs/docs/02-architecture.md)** — the scale-out sequence diagram gains the assignment and forward hops.
- **New: operations notes** — the SQL scripts are vendored and operator-applied (finding 8); cluster sizing guidance for `ServerConnectionsPerHub` relative to node count.
- **[00-review-findings.md](../docs/docs/00-review-findings.md)** — a Phase 3 results entry in the same format as Phases 0–2, including the `connectionToken` correction.
- **[CLAUDE.md](../CLAUDE.md)** — Project Status and the registry/backplane architecture notes.

---

## 8. Risks

| Risk | Mitigation |
|---|---|
| The fan-out inversion (D14) silently changes single-node semantics | Slice 0 is behavior-preserving by definition — zero test changes, and any test needing one is treated as a defect |
| Orleans grain latency lands on the per-message hot path | Assignment happens once at accept (D18); every send tries the node-local path first (D17/Slice 3); the registry is never enumerated per message (D14). Benchmarked properly in Phase 5 |
| A dead node's observer is retried forever, or a deactivated grain silently stops all cross-node delivery | Both are **verified** failure modes (findings 3, 5), each with a dedicated inducing test in Slice 2 rather than a code comment |
| `void` observer methods are used because ADR-003 says so | D16 changes the signature *and* the ADR; the eviction test cannot pass with `void` methods, so the build enforces it |
| A clustered deployment 401s on connect because the pending-connection store is node-local | D19, landed in **Slice 1** with the other state substitutions; from Slice 2 onward every two-node test negotiates on one node and connects on the other, so a regression fails immediately rather than at the milestone |
| SSE/Long Polling forwarding proves messier than expected | Documented fallback: session affinity for those two transports only, WebSocket unaffected — recorded in `00-review-findings.md` if taken, not silently adopted |
| A node with no app-server connections rejects healthy traffic | D18 makes assignment cluster-wide; the Slice 4 gate tests the zero-local-connections node explicitly |
| The forward hop is extended to negotiate, quietly turning D11's `TrustedProxyNetworks` allowlist into "trusted from anywhere" | D19 states that negotiate never forwards and why; the Pattern A tests from Phase 2 Slice 8 are run again in clustered mode, where a spoofing peer is now two hops from the evaluating node |
| `ServerConnectionId` becomes node-qualified and the two dictionary lookups silently miss instead of throwing | Finding 12 names both sites; the `ServerConnectionRef` helper lands in Slice 0, one slice before the format changes |
| `/healthz` starts doing cluster I/O on every load-balancer probe | Slice 7 caches the cluster answer; a readiness endpoint that depends on cluster health fails hardest exactly when it matters |
| ADO.NET persistence is assumed working because the code compiles | Slice 6 gates on a real database, and explicitly reports "untested" rather than "done" if none is available |
| The in-memory registry rots while attention is on Orleans | D20's shared conformance suite plus running the whole suite in both modes |
| Grain state `[Id(n)]` is reordered, breaking a rolling upgrade | Called out as a wire contract equal to `[Key(n)]`, pinned by a serialization test |
| Phase 3 accidentally implements Phase 4 (management API, metrics) | Grain state makes cluster-wide queries easy and tempting; no slice adds an endpoint, and `/healthz` stays the public no-detail probe it already is |

---

## 9. Definition of done

Per [06-project-plan.md § Definition of Done](../docs/docs/06-project-plan.md):

1. All 13 Phase 3 deliverables implemented.
2. All existing tests still pass, in **both** `UseOrleansCluster` modes.
3. Phase 3 integration tests added and passing, including the two-node out-of-process test.
4. **Milestone:** a rolling restart of one node drops no connection that reconnects within the window; broadcasts cross nodes; no Redis anywhere.
5. No unresolved TODO/FIXME in new code.
6. Documentation updates in §7 applied — including the three corrections (observer signature, `connectionToken` node encoding, package table) that this plan's verification pass turned up.
