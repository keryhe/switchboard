# Detailed Component Design

## 1. Negotiation Service

### Responsibility
Handle `POST /{hub}/negotiate`. This drives the standard **two-step SignalR redirect** (see [Protocol §1.1](03-protocol.md#11-negotiation)): **step 1** issues a short-lived JWT and returns this service's `https://` URL as a redirect response; **step 2** — the client re-negotiates against this service presenting that token — mints the opaque `connectionToken` and returns the transport list.

### JWT Claims
```
{
  "connectionId": "<uuid>",   // connection identity (public; surfaced to hub code as Context.ConnectionId)
  "sub": "<userId>",          // set if the app server forwarded user identity; omitted if anonymous
  "hubName": "chatHub",       // target hub
  "iss": "switchboard",   // configurable issuer
  "aud": "switchboard-client",    // client audience
  "iat": 1700000000,
  "exp": 1700000060           // 60 second TTL
}
```

> **Custom claims, private token.** `connectionId` and `hubName` are custom claims; `sub` is the standard subject claim carrying the userId. This token is issued and validated solely by this service and is opaque to SignalR client libraries (they never parse it), so the claim names are an internal contract with no wire-compatibility constraint. `connectionId` is a distinct claim rather than an overload of `jti` — see [connectionToken vs connectionId](05-data-models.md#client-connection-state).

### Two Negotiation Patterns

**Pattern A — Service-Direct Negotiate (Phase 2; optional, disabled by default)**  
Client hits the service negotiate endpoint directly, with no app server in the loop — for internal daemons or deployments where an authenticating reverse proxy or API gateway already fronts the service.

The flow is **identical to Pattern B, with the service playing both roles**: step 1 returns the same redirect response `{url, accessToken}` pointing at the service itself, and the client re-negotiates (step 2) and connects exactly as always. This is deliberate — a non-redirect negotiate response has no field capable of carrying an access token to the client, so returning the connection response in one hop would force a second transport-authentication path in [§2](#2-client-connection-lifecycle) with `connectionToken` acting as a bearer secret. Reusing the redirect keeps token issuance, transport auth, and the Accept Phase unchanged; the only thing that differs is **where the identity comes from**.

*Identity source.* Instead of an app server's forwarded call, the service reads the user identity from trusted request headers — the same headers the Connector sends ([§8](#8-connector--negotiate-interception)):

```
X-Switchboard-UserId: alice
X-Switchboard-Claims: <base64 claims>
```

*Configuration (see [`SwitchboardOptions`](05-data-models.md#configuration-models)):*

```csharp
public bool EnableDirectNegotiate { get; set; } = false;
public string TrustedIdentityHeader { get; set; } = "X-Switchboard-UserId";
public string TrustedClaimsHeader   { get; set; } = "X-Switchboard-Claims";
public string[] TrustedProxyNetworks { get; set; } = [];   // CIDR, e.g. ["10.0.0.0/8"]
```

*Security rules — all mandatory:*

1. **Disabled by default.** With `EnableDirectNegotiate = false`, a negotiate request bearing no valid step-1 access token is rejected `401`. Pattern B is unaffected.
2. **Fail fast on misconfiguration.** If `EnableDirectNegotiate` is `true` and `TrustedProxyNetworks` is empty, the service **must refuse to start** — the same treatment as a missing `PublicUrl`. An identity header trusted from anywhere is a trivial impersonation vector: any client could assert `X-Switchboard-UserId: admin`.
3. **Allowlist enforced per request.** The identity headers are honored only when the peer address falls inside `TrustedProxyNetworks`. On every other request they are **stripped before processing** and the connection is treated as anonymous — never merely ignored, so a spoofed header cannot survive into claims.
4. **Evaluate against the immediate peer.** Match on the directly connected peer address, not a `X-Forwarded-For` value, unless ASP.NET Core's `ForwardedHeadersMiddleware` is configured with its own `KnownProxies`/`KnownNetworks` allowlist — otherwise the check is spoofable by the very header it is meant to guard.

*Evaluation order (Phase 2, implemented).* A single `POST /{hub}/negotiate` request is dispatched in this exact order — Pattern A's allowlist is consulted only once neither token validates, and only when `EnableDirectNegotiate` is on:

1. Valid server token (`ServerSigningKey`, `role: appserver`) → Pattern B step 1; identity headers are trusted **unconditionally** — the trust boundary here is the token, never the network, so a server-token request is never subject to the `TrustedProxyNetworks` allowlist even from a peer outside it.
2. Valid client token (`TokenSigningKey`) → step 2, as always.
3. No valid token, `EnableDirectNegotiate` on, peer inside `TrustedProxyNetworks` → Pattern A step 1; identity headers trusted.
4. No valid token, `EnableDirectNegotiate` on, peer outside `TrustedProxyNetworks` → Pattern A step 1, but **anonymous** — headers stripped (rule 3 above), no `401`. This surprises people: enabling Pattern A never turns an untrusted request away at this point, it just downgrades it to anonymous. A connection is still issued.
5. Otherwise → `401`.

> **Pattern B remains the recommended path** for any deployment with an ASP.NET Core app server. Pattern A trades an application-level trust boundary for a network-level one, and is only as strong as the network isolation behind it.

**Pattern B — App-Server-Forwarded Negotiate (recommended)**  
Client hits the app server's negotiate endpoint. The app server authenticates the user, then calls the service negotiate endpoint with the user's identity. The service issues a token with `sub` set and returns a redirect. The app server returns the redirect to the client, which then re-negotiates against the service (step 2) before connecting.

```
Step 1 — redirect issuance:
  Client → App Server (authenticate user)
  App Server → Service Negotiate (POST with userId/claims)
  Service → App Server (redirect: https url + JWT with userId claim)
  App Server → Client (same redirect: url + JWT)
Step 2 — connect:
  Client → Service Negotiate (POST with Bearer JWT)
  Service → Client (connectionId + connectionToken + availableTransports)
  Client → Service Transport (id=connectionToken, access_token=JWT)
```

### Interface
```csharp
public interface INegotiationService
{
    // Step 1 — issue the redirect: mint the access-token JWT (binding connectionId, hub, and any
    // user identity) and return this service's https URL. Called by the app server's negotiate
    // interceptor (Pattern B) or the service-direct endpoint (Pattern A).
    Task<RedirectResponse> IssueRedirectAsync(string hubName, string? userId, IEnumerable<Claim>? claims, CancellationToken ct);

    // Step 2 — the client re-negotiates here presenting the step-1 token; mint the opaque
    // connectionToken and return the transport list.
    Task<NegotiateResponse> NegotiateAsync(string hubName, ClaimsPrincipal accessToken, CancellationToken ct);
}

public record RedirectResponse(
    string Url,                 // https:// URL of this service for {hubName}
    string AccessToken          // short-lived JWT — see JWT Claims above
);

public record NegotiateResponse(
    string ConnectionId,        // public identity
    string ConnectionToken,     // opaque transport handle (the `id` query param)
    int NegotiateVersion,
    IReadOnlyList<AvailableTransport> AvailableTransports
);
```

> **Dispatching step 1 vs step 2 (Phase 1, resolved).** Both steps hit the same `POST /{hub}/negotiate` URL. The endpoint dispatches on the **validated token type**, never on a caller-controlled query parameter or header: a token that validates against `ServerSigningKey` with `role: appserver` and `aud: switchboard-server` is step 1 (issue a redirect); a token that validates against `TokenSigningKey` with `aud: switchboard-client` is step 2 (issue the connect response). Anything else is `401`. This is a security-sensitive dispatch — getting it wrong in either direction lets a client token mint redirects or an app-server token claim a client connection — so each row, including the negative case, has a dedicated test (`Keryhe.Switchboard.UnitTests.Negotiate.NegotiateEndpointTests`).
>
> **Pending-connection store (Phase 1).** Step 2 mints the opaque `connectionToken` and must remember it until the client presents it on the transport upgrade. A TTL'd in-memory map (`IPendingConnectionStore`, keyed by `connectionToken`, TTL matched to `ClientTokenExpiry`) bridges the gap, with a background reaper evicting expired entries. A transport upgrade presenting an unknown or expired token gets `401`/`404`, never a new connection.
>
> **No app servers registered (Phase 1).** Step 2 fails fast rather than waiting/queueing: if `IHubRegistry` has no active server connection for the hub, negotiate returns `503` immediately (the earliest point the service can know) instead of accepting a transport it cannot service. Waiting/queueing for a server connection to appear is a Phase 3 resilience concern.

---

## 2. Client Connection Lifecycle

### States
```
[Connecting] → [Handshaking] → [Connected] → [Disconnecting] → [Disconnected]
```

### Accept Phase
1. Service validates the JWT from the `access_token` query parameter and reads the `connectionToken` from the `id` query parameter
2. Extracts `connectionId` and `hubName` from JWT claims, and resolves the `connectionToken` to this connection (in Phase 3 the token also identifies the owning node, so a request landing on a non-owning node can be routed appropriately)
3. Registers a **pending** connection in `IConnectionRegistry` with `HubProtocol = null`
4. Registers the local `IClientTransport` handle in `ILocalTransportRegistry`
5. Finds (or waits for) an available server connection in the Hub Registry

### Handshake Phase
1. Service sends the hub protocol handshake request to the client
2. Client responds with its chosen protocol (`json` or `messagepack`)
3. Service updates the connection's `HubProtocol` in `IConnectionRegistry` via `SetProtocolAsync(connectionId, protocol)`
4. Service sends `open_connection` notification to the assigned app server — this is the first point at which the app server is aware of the connection

> **Why two phases:** `HubProtocol` is only known after the client responds to the handshake. The registry registration must happen before the handshake (to reserve the connection slot and assign a server connection), so `HubProtocol` is nullable and updated in a second call after the handshake completes.

### Message Loop
Two concurrent loops run per client connection, plus a third for keep-alive (Phase 2):

**Read loop:** Read frames from client transport → classify → route to Message Router → forward to app server via server connection envelope. A hub-level `Ping` (plan decision **D13**) is **absorbed here, never forwarded** — the service owns client keep-alive end to end, so a client's own keep-alive `Ping` never reaches the app server as a spurious `client_message`.

**Write loop:** Dequeue messages from per-connection write channel → serialize using client's hub protocol → write frame to client transport.

**Keep-alive ping loop (Phase 2, implemented):** a `PeriodicTimer` on `ClientKeepAliveInterval` writes a hub-level `Ping` to the client for the lifetime of the connection, independent of whether the app server or the client itself is sending anything. **This did not exist until Phase 2 Slice 9** — `ClientKeepAliveInterval` was declared in `SwitchboardOptions` back in Phase 1 but never wired to anything, so an idle-but-healthy connection would silently trip the real client's own `ServerTimeout` (30s default) and reconnect needlessly; found by running the Angular sample in a real browser rather than by any automated test, since every prior end-to-end test completes in well under a second. See [00-review-findings.md § Phase 2 Slice 9](00-review-findings.md).

The write channel provides backpressure isolation: the read loop never blocks waiting for a slow write.

### Disconnect Phase
1. Transport signals EOF or close
2. Service sends `close_connection` notification to app server
3. Connection removed from `IConnectionRegistry` and all group memberships
4. Write channel completed; write loop exits

### Key Abstractions
```csharp
public interface IClientTransport
{
    string ConnectionId { get; }
    string HubName { get; }
    string? UserId { get; }
    Channel<HubMessage> Output { get; }   // messages to write to client
    IAsyncEnumerable<HubMessage> ReadAllAsync(CancellationToken ct);
    ValueTask CloseAsync(string? error = null);
}
```

---

## 3. Server Connection Manager

### Pool Per Hub
Each hub maintains a configurable number of physical WebSocket connections to app servers:
```
chatHub → [ServerConn-A1, ServerConn-A2, ServerConn-A3, ServerConn-A4, ServerConn-A5]
          [ServerConn-B1, ServerConn-B2, ...]   ← connections to second app server
```

### App Server Registration
App servers register by opening a WebSocket to `wss://service/server/{hubName}` and completing the handshake. The service adds the new server connection to the Hub Registry. Multiple connections from the same app server are all registered independently — they are equivalent from the router's perspective.

### Load Distribution for Client Assignment
When a new client connects, the router assigns it to a server connection. Default policy: weighted round-robin based on current logical connection count per physical connection. This keeps load balanced and allows the SDK to control concurrency per physical connection.

```csharp
public interface IServerConnectionSelector
{
    ServerConnection? SelectConnection(string hubName);
}
```

### Health Monitoring
Each server connection runs a background ping loop:
- Send `{"type":"ping"}` every 15 seconds
- Expect `{"type":"pong"}` within 5 seconds
- On timeout: mark connection degraded, attempt reconnect
- If the server connection drops, clients assigned to it receive a `Close` message with `allowReconnect: true`, triggering reconnect through the negotiate flow

### Server Connection Interface
```csharp
public interface IServerConnection
{
    string ConnectionId { get; }
    string HubName { get; }
    int LogicalConnectionCount { get; }
    ValueTask SendAsync(ServerEnvelope envelope, CancellationToken ct);
    IAsyncEnumerable<ServerEnvelope> ReadAllAsync(CancellationToken ct);
}
```

---

## 4. Connection Registry Design

### Single-Node (Phase 1)

In-memory registry using concurrent collections:

```csharp
public class InMemoryConnectionRegistry : IConnectionRegistry
{
    private readonly ConcurrentDictionary<string, ClientConnectionState> _connections = new();
    private readonly ConcurrentDictionary<string, ConcurrentHashSet<string>> _groupMembers = new();
    private readonly ConcurrentDictionary<string, ConcurrentHashSet<string>> _userConnections = new();
}

public record ClientConnectionState(
    string ConnectionId,
    string HubName,
    string? UserId,
    string ServerConnectionId,
    IClientTransport Transport,
    DateTimeOffset ConnectedAt
)
{
    public ConcurrentHashSet<string> Groups { get; } = new();
}
```

### Distributed (Phase 3)

Orleans grain-based registry. Each grain type owns one slice of the connection state:

- `IConnectionGrain` (key: `connectionId`) — stores `{ownerNodeId, hubName, userId, connectedAt}`; looked up on every targeted `send_to_connection`
- `IHubGrain` (key: `hubName`) — stores the full set of `{connectionId, nodeId}` pairs; iterated for broadcasts
- `IGroupGrain` (key: `"hubName::groupName"`) — stores the set of `connectionId` values in the group
- `IUserGrain` (key: `"hubName::userId"`) — stores the set of `connectionId` values for a user

`OrleansConnectionRegistry` implements `IConnectionRegistry` by delegating to these grains via `IGrainFactory`. Local transport handles (`IClientTransport`) are never stored in grain state — they are kept in a node-local `ILocalTransportRegistry` (a `ConcurrentDictionary` in a singleton service) and looked up by `connectionId` after grain calls return the owner node ID.

### Interface
```csharp
public interface IConnectionRegistry
{
    Task RegisterAsync(ClientConnectionState state, CancellationToken ct);
    Task SetProtocolAsync(string connectionId, string hubProtocol, CancellationToken ct);  // called after handshake
    Task UnregisterAsync(string connectionId, CancellationToken ct);
    Task<ClientConnectionState?> GetAsync(string connectionId, CancellationToken ct);
    IAsyncEnumerable<ClientConnectionState> GetAllAsync(string hubName, CancellationToken ct);
    Task AddToGroupAsync(string connectionId, string groupName, CancellationToken ct);
    Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken ct);
    IAsyncEnumerable<ClientConnectionState> GetGroupMembersAsync(string hubName, string groupName, CancellationToken ct);
    IAsyncEnumerable<ClientConnectionState> GetUserConnectionsAsync(string hubName, string userId, CancellationToken ct);
}
```

---

## 5. Message Router Design

The router is the central dispatch engine. It operates purely on resolved `ClientConnectionState` and `IServerConnection` references — it does not know about transports or serialization details.

### Routing Operations

```csharp
public interface IMessageRouter
{
    // Client sent a message; route to the assigned app server
    ValueTask RouteClientMessageAsync(string connectionId, ReadOnlyMemory<byte> payload, string hubProtocol, CancellationToken ct);

    // App server sent a targeted message; route to the specific client
    ValueTask RouteToConnectionAsync(string connectionId, ReadOnlyMemory<byte> payload, string hubProtocol, CancellationToken ct);

    // App server broadcasts; fan out to all hub clients. payloadsByProtocol carries one entry per
    // hub protocol (plan decision D7, implemented Phase 2 Slice 3) — a broadcast/group/user send
    // doesn't know each recipient's protocol in advance, so the Connector always sends both a
    // "json" and a "messagepack" encoding; the router picks whichever entry matches each
    // recipient's own negotiated protocol. payload/hubProtocol remain the single-protocol
    // fallback for envelopes without payloadsByProtocol. A target whose protocol has no matching
    // entry is skipped and logged — never sent the wrong bytes.
    ValueTask BroadcastAsync(string hubName, ReadOnlyMemory<byte> payload, string hubProtocol, IReadOnlyDictionary<string, byte[]>? payloadsByProtocol, IReadOnlySet<string>? excludedConnectionIds, CancellationToken ct);

    // Fan out to group members — same mixed-protocol handling as BroadcastAsync.
    ValueTask SendToGroupAsync(string hubName, string groupName, ReadOnlyMemory<byte> payload, string hubProtocol, IReadOnlyDictionary<string, byte[]>? payloadsByProtocol, IReadOnlySet<string>? excludedConnectionIds, CancellationToken ct);

    // Fan out to all connections for a user — same mixed-protocol handling as BroadcastAsync.
    ValueTask SendToUserAsync(string hubName, string userId, ReadOnlyMemory<byte> payload, string hubProtocol, IReadOnlyDictionary<string, byte[]>? payloadsByProtocol, CancellationToken ct);
}
```

> **`RouteToConnectionAsync` carries only a single `payload`/`hubProtocol`, never `payloadsByProtocol`** — a targeted send has exactly one recipient, whose protocol the sender already knows (03-protocol.md §2.3).

### Fan-out Strategy (Phase 1/2, implemented)

Group and user fan-out (Phase 1 deliverable; deferred no further after plan decision **D3**'s
Phase 1 log-and-no-op placeholder) and broadcast fan-out all route through the same node-local
path: the registry resolves the hub/group/user to a set of `ClientConnectionState`, each connection
picks its own payload from `payloadsByProtocol` (or falls back to `payload`) via its recorded
`hubProtocol`, and the write goes through that connection's bounded per-connection write channel
(`WriteChannelCapacity`/`WriteChannelFullMode`, default `DropWrite`) — never a blocking write, so
one slow client cannot stall fan-out to every other client. This is explicitly **node-local**: all
of it goes through `ILocalTransportRegistry`, and cross-node fan-out remains `IBackplane`'s
job — still `NoOpBackplane` in Phase 2, unchanged from Phase 1. The batched/partitioned
`Parallel.ForEachAsync` large-hub strategy originally sketched here remains a Phase 3+ scaling
concern, not yet needed at Phase 2's tested scale.

---

## 6. Transport Abstraction Layer

Implemented (Phase 2, plan decision **D10**) as a layered interface hierarchy rather than one flat
`IClientTransport`, because SSE and Long Polling need capabilities WebSocket doesn't:

```csharp
// Core — transport-agnostic: an output pipe for the router to write into, and a byte stream to
// read inbound frames from. Every transport implements this.
public interface IClientTransport : IAsyncDisposable
{
    Pipe Output { get; }
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(CancellationToken ct);
    Task CloseAsync(string? error = null);
}

// Server — adds what the handshake/lifecycle needs to know about the physical transport itself.
public interface IFramedClientTransport : IClientTransport
{
    IHubProtocolFraming Framing { get; set; }       // switched to the negotiated protocol post-handshake
    bool SupportsBinaryTransferFormat { get; }       // false for SSE — gates messagepack at handshake
}

// Server — SSE and Long Polling only: their inbound frames arrive over a *separate* HTTP request
// (POST) from whichever request is currently serving output (the open GET for SSE, the current
// poll for Long Polling) — WebSocket doesn't need this, since one socket carries both directions.
public interface IPostableClientTransport : IFramedClientTransport
{
    Task FeedAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
}
```

`ClientConnectionLifecycle` (handshake negotiation, registration, the message loop, teardown, and
the keep-alive ping loop) is written once against `IFramedClientTransport`/`IPostableClientTransport`
and reused unchanged by all three transports — only how bytes physically get in and out differs:

### WebSocket Transport
`WebSocketClientTransport` (`IFramedClientTransport`, `SupportsBinaryTransferFormat: true`). Uses
`System.Net.WebSockets.WebSocket` (Kestrel's managed WebSocket); reads are processed via
`System.IO.Pipelines` for zero-copy frame parsing. Its entire lifecycle is one HTTP request — the
upgraded connection itself.

### SSE Transport
`SseClientTransport` (`IPostableClientTransport`, `SupportsBinaryTransferFormat: false` — Text
only, 03-protocol.md §1.5). The establishing GET stays open for the connection's life, writing
`Output`'s frames out as `data: ...` lines; a separate POST feeds inbound frames via `FeedAsync`.
Response headers must be explicitly flushed after `StartAsync` (`03-protocol.md` §1.5) — Kestrel
does not do this implicitly, and the real client's transport hangs waiting for them otherwise.

### Long Polling Transport
`LongPollingClientTransport` (`IPostableClientTransport`, `SupportsBinaryTransferFormat: true`).
Unlike the other two, its lifecycle spans many independent short-lived HTTP requests rather than
one long-lived one: the establishing GET registers the transport and returns immediately (the real
client's `StartAsync` only checks the status code); the handshake and message loop then run
detached on a background task, fed by later POSTs (`FeedAsync`) and drained by later polling GETs
(`PollAsync`, `03-protocol.md` §1.6) — a plain timeout returns 200/empty, never 204, and 204 is
reserved for genuine closure. A background `LongPollingReaperService` closes any transport whose
last poll exceeds `DisconnectTimeout`, since there is no socket-close event to notice abandonment.

---

## 7. Backplane Design (Phase 3)

### Problem
In a clustered deployment with multiple service nodes, a broadcast from an app server connected to Node A must also reach clients connected to Node B.

### Solution: Orleans Grain Observers

Each service node registers a local `HubObserverImpl` object — implementing the Orleans `IGrainObserver` interface `IHubObserver` — with the relevant `IHubGrain` at startup. When a grain needs to notify all nodes (broadcast, group, user), it calls each registered observer. Because observers are references to live objects on the silo that registered them, each observer runs in its own silo and has direct access to `ILocalTransportRegistry` to write to local client transports.

No stream provider infrastructure is required. Grain observers are a built-in Orleans primitive included in `Microsoft.Orleans.Server`.

**Message flow (broadcast example):**
```
AppServer → ServiceNodeA → local fan-out (ILocalTransportRegistry)
                         → IHubGrain.BroadcastAsync(payload, originNodeId: "node-a")
                               → observer on Node B (skips Node A — origin)
                               → observer on Node C (skips Node A — origin)
                         NodeB.HubObserverImpl.OnBroadcast(payload)
                         → ILocalTransportRegistry → local clients on Node B
```

**Self-echo prevention:** `BroadcastAsync` accepts `originNodeId`. The hub grain maintains `Dictionary<string, IHubObserver> _observers` keyed by `nodeId`. When broadcasting, it skips the observer registered under `originNodeId` — that node already handled its local clients before calling into the grain.

**Targeted `send_to_connection`:** The router calls `IConnectionGrain.GetOwnerNodeAsync(connectionId)` to find the owning node ID, then calls `IHubObserver.OnConnectionMessage(connectionId, payload)` on the observer for that node. No broadcast; single observer call.

### IHubObserver — Per-Node Observer Interface

```csharp
public interface IHubObserver : IGrainObserver
{
    void OnBroadcast(byte[] payload, string hubProtocol, string[] excludedConnectionIds);
    void OnGroupMessage(string groupName, byte[] payload, string hubProtocol, string[] excludedConnectionIds);
    void OnUserMessage(string userId, byte[] payload, string hubProtocol);
    void OnConnectionMessage(string connectionId, byte[] payload, string hubProtocol);
}
```

`HubObserverImpl` is a concrete class (not a grain — it is a plain object) that the service node registers with each hub grain. It holds a reference to `ILocalTransportRegistry` injected at construction time.

### Orleans Grain Interfaces

```csharp
public interface IHubGrain : IGrainWithStringKey          // key: hubName
{
    Task RegisterConnectionAsync(string connectionId, string nodeId);
    Task UnregisterConnectionAsync(string connectionId);
    Task SubscribeAsync(IHubObserver observer, string nodeId);
    Task UnsubscribeAsync(string nodeId);
    Task BroadcastAsync(byte[] payload, string hubProtocol, string[] excludedConnectionIds, string originNodeId);
    Task SendToGroupAsync(string groupName, byte[] payload, string hubProtocol, string[] excludedConnectionIds, string originNodeId);
    Task SendToUserAsync(string userId, byte[] payload, string hubProtocol, string originNodeId);
    Task SendToConnectionAsync(string connectionId, byte[] payload, string hubProtocol);
}

public interface IGroupGrain : IGrainWithStringKey         // key: "hubName::groupName"
{
    Task AddAsync(string connectionId, string nodeId);
    Task RemoveAsync(string connectionId);
}

public interface IUserGrain : IGrainWithStringKey          // key: "hubName::userId"
{
    Task AddConnectionAsync(string connectionId, string nodeId);
    Task RemoveConnectionAsync(string connectionId);
}

public interface IConnectionGrain : IGrainWithStringKey   // key: connectionId
{
    Task SetOwnerAsync(string nodeId, string hubName, string? userId);
    Task<string?> GetOwnerNodeAsync();
    Task ClearAsync();
}
```

> **Group and User grains:** `IGroupGrain` and `IUserGrain` store `{connectionId, nodeId}` pairs so the hub grain can resolve node ownership without additional `IConnectionGrain` lookups for fan-out. When a connection joins a group, the node ID is passed alongside the connection ID.

### Backplane Interface
```csharp
public interface IBackplane
{
    Task PublishBroadcastAsync(string hubName, byte[] payload, string hubProtocol, string[] excludedConnectionIds, CancellationToken ct);
    Task PublishGroupMessageAsync(string hubName, string groupName, byte[] payload, string hubProtocol, string[] excludedConnectionIds, CancellationToken ct);
    Task PublishUserMessageAsync(string hubName, string userId, byte[] payload, string hubProtocol, CancellationToken ct);
    Task PublishToConnectionAsync(string connectionId, byte[] payload, string hubProtocol, CancellationToken ct);
}
```

---

## 8. Connector — Negotiate Interception

`Keryhe.Switchboard.Connector` must intercept the SignalR negotiate endpoint on the app server and return a redirect response pointing to the proxy service. `IHubLifetimeManager` does not control negotiate — negotiate is handled inside ASP.NET Core's endpoint-routing pipeline, separately from the hub lifetime manager. The Connector overrides it at the routing layer.

> **Scope.** This section covers only the *outbound* half of the Connector — diverting negotiate. Turning inbound `open_connection` / `client_message` / `close_connection` envelopes back into real Hub method invocations is [§11 — Inbound Dispatch](#11-connector--inbound-dispatch-synthetic-client-connections).

### Mechanism — `MatcherPolicy` on the negotiate endpoint

In ASP.NET Core SignalR, `MapHub<T>()` maps the `/{hub}/negotiate` route to `HttpConnectionDispatcher`, which processes negotiate **inline** (`ExecuteNegotiateAsync` → `ProcessNegotiate`). There is no `NegotiateHandler` service resolved from DI, so negotiate cannot be overridden by registering a replacement service — the dispatcher and its negotiate logic are internal and not DI-swappable.

The supported extensibility point is ASP.NET Core **endpoint routing**. The Connector registers a `MatcherPolicy` that targets negotiate endpoints and swaps their delegate for one that returns the proxy redirect. This is the same mechanism the Azure SignalR SDK uses (its `NegotiateMatcherPolicy`).

```csharp
// Inside AddSwitchboardConnector():
services.TryAddEnumerable(
    ServiceDescriptor.Singleton<MatcherPolicy, SwitchboardNegotiateMatcherPolicy>());
```

`SwitchboardNegotiateMatcherPolicy` implements `MatcherPolicy` + `IEndpointSelectorPolicy`:

1. **`AppliesToEndpoints(endpoints)`** — returns `true` for endpoints carrying `NegotiateMetadata` (the marker `MapHub<T>()` attaches to the negotiate endpoint it creates). All other endpoints are ignored.
2. **`ApplyAsync(HttpContext, CandidateSet)`** — for the matched negotiate candidate, replaces its endpoint with one whose `RequestDelegate` runs the redirect logic below, so the framework's default negotiate delegate never executes.

> ⚠️ **The replacement endpoint MUST carry the original endpoint's metadata.** `MapHub<THub>()` copies the hub type's attributes onto the endpoint (`typeof(THub).GetCustomAttributes(inherit: true)` → `e.Metadata.Add(item)`), which is how class-level `[Authorize]` on a Hub is enforced — by `AuthorizationMiddleware` reading endpoint metadata on the HTTP request, **not** inside `HubConnectionHandler`. `DefaultHubDispatcher` enforces only *per-method* `[Authorize]` and performs no class-level check on connect.
>
> In this topology the client's transport request goes to Switchboard and never reaches the app server, so **negotiate is the only surviving enforcement point** for class-level `[Authorize]`. If `ApplyAsync` swaps in a bare endpoint, authorization is silently dropped and `[Authorize] public class ChatHub` (as used in [the sample](08-sample-app.md)) stops being enforced with no error. Build the replacement from the original endpoint's `Metadata` collection, and assert this in the Phase 0 spike.
> (Verified against [HubEndpointRouteBuilderExtensions.cs](https://github.com/dotnet/aspnetcore/blob/main/src/SignalR/server/SignalR/src/HubEndpointRouteBuilderExtensions.cs) and [DefaultHubDispatcher.cs](https://github.com/dotnet/aspnetcore/blob/main/src/SignalR/server/Core/src/Internal/DefaultHubDispatcher.cs).)

The redirect `RequestDelegate` does the following:

1. Reads the authenticated user's identity from `HttpContext.User` (claims, userId)
2. Makes an HTTP POST to the proxy service's negotiate endpoint:
   ```
   POST {ServiceUrl}/{hubName}/negotiate
   Authorization: Bearer <server access token>
   X-Switchboard-UserId: {userId}           // optional, if user is authenticated
   X-Switchboard-Claims: {base64 claims}    // optional, custom claims to embed
   ```
3. Receives the redirect `{url, accessToken}` from the proxy (step 1; `availableTransports` and the connection identifiers come from the client's step-2 negotiate against the proxy)
4. Writes it directly as the negotiate response to the calling client, which follows the redirect and re-negotiates against the proxy

The client (Angular `@microsoft/signalr`) follows the redirect automatically — it receives the url+token and opens its WebSocket directly to the proxy service.

> **Hub name resolution.** The policy derives `{hubName}` from the matched route (or from `NegotiateMetadata`/hub endpoint metadata), so a single policy registration covers every hub mapped with `MapHub<T>()` without per-hub wiring.

### HTTP Client Configuration

The Connector uses `IHttpClientFactory` to make calls to the proxy service. The named client `"switchboard-negotiate"` is configured in `AddSwitchboardConnector()`:

```csharp
services.AddHttpClient("switchboard-negotiate", client =>
{
    client.BaseAddress = new Uri(options.ServiceUrl);
    client.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Bearer", options.ServerAccessToken);
    client.Timeout = TimeSpan.FromSeconds(5);
});
```

### `SwitchboardConnectorOptions`

```csharp
public sealed class SwitchboardConnectorOptions
{
    public required string ServiceUrl { get; set; }         // e.g. "https://signalr-proxy.internal"
    public required string ServerAccessToken { get; set; }  // long-lived server JWT
    public int ServerConnectionsPerHub { get; set; } = 5;  // physical WebSocket connections
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(5);
    public int MaxReconnectAttempts { get; set; } = 0;      // 0 = unlimited
}
```

### Startup Connection Behaviour

On `IHostedService.StartAsync`, the Connector opens `ServerConnectionsPerHub` WebSocket connections to the proxy for each hub registered via `MapHub<T>()`. If the proxy is unavailable at startup, the Connector retries with exponential backoff (base: `ReconnectDelay`, max: 60 seconds) up to `MaxReconnectAttempts` times. During the retry window, the `/healthz` endpoint on the app server should return degraded/unhealthy until at least one server connection is established.

---

## 9. CORS Configuration

The proxy service's negotiate endpoint and WebSocket endpoint are called from browser clients (Angular). Without correct CORS headers, browsers will block the negotiate preflight and the WebSocket upgrade.

### Service-Side CORS Policy

Configured in `SwitchboardOptions`:

```csharp
public sealed class SwitchboardOptions
{
    // ... existing options ...
    public string[] AllowedOrigins { get; set; } = [];   // e.g. ["https://localhost:4200", "https://app.example.com"]
}
```

Registered in `Program.cs`:

```csharp
builder.Services.AddCors(options =>
{
    options.AddPolicy("Switchboard", policy =>
    {
        policy
            .WithOrigins(switchboardOptions.AllowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();  // required if the Angular app uses cookie-based auth
    });
});

// Must be placed before UseRouting / MapHub
app.UseCors("Switchboard");
```

> **`AllowCredentials()` requires explicit origins.** Wildcard `"*"` cannot be combined with `AllowCredentials()`. `AllowedOrigins` must list every origin that the Angular app will be served from (including `localhost` variants for development).

### What Needs CORS

| Endpoint | Why |
|---|---|
| `POST /hubName/negotiate` | Browser sends preflight OPTIONS before the negotiate POST |
| `POST` / `DELETE /hubName` (SSE/Long Polling send/close) | Preflight required; covered by the same `app.UseCors("Switchboard")` default policy — verified directly (`CorsAndOriginEndToEndTests`), not assumed |
| `GET /api/v1/...` (management API) | Only if called from a browser; typically not |

> **`GET /hubName` (WebSocket upgrade) is deliberately *not* in this table — it is not a CORS-protected request at all.** Browsers do not preflight a WebSocket upgrade and never enforce `Access-Control-Allow-Origin` on one; `app.UseCors` has no effect on it (a risk called out explicitly in the Phase 2 plan, since it's easy to assume otherwise). The upgrade handler (`ClientConnectionValidation.IsOriginAllowed`, called from `ClientConnectionEndpoint`) enforces `AllowedOrigins` itself instead: a request whose `Origin` header doesn't match the allowlist is rejected `403` before any token validation runs. A request with **no** `Origin` header at all (the .NET client, and any other non-browser caller) is allowed through regardless of `AllowedOrigins` — only browsers send `Origin`, and the point of this check is to stop a browser page on an unlisted origin from opening a socket directly, not to gate non-browser clients.

### App-Server-Side CORS (SampleChatApp.Api)

The API's `/chatHub/negotiate` endpoint (mapped at the bare `/chatHub` — see the route-correction note in [08-sample-app.md](08-sample-app.md)) is called by Angular. Standard ASP.NET Core CORS applies:

```csharp
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngular", policy =>
        policy.WithOrigins("https://localhost:4200")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials());
});
app.UseCors("AllowAngular");
```

In development, the Angular CLI proxy (`proxy.conf.json`) forwards requests to the API, making CORS unnecessary for local development. CORS on the API only matters for deployed environments.

---

## 10. Performance Considerations

### System.IO.Pipelines
All message reading uses `PipeReader` to avoid buffer allocation per message. The pipeline processes frames without copying bytes until a complete message is assembled.

### Channel-Based Write Queues
Each client connection has a `Channel<ReadOnlyMemory<byte>>` for outbound messages. This decouples the router's fan-out from the actual write I/O. `BoundedChannel` with `FullMode.DropOldest` or `DropWrite` provides backpressure control.

### ArrayPool<byte> / MemoryPool<byte>
Message payloads are rented from `ArrayPool<byte>.Shared` and returned after serialization to avoid GC pressure during high-throughput fan-out.

### ObjectPool for Envelope Wrappers
`ServerEnvelope` and `ClientEnvelope` objects are pooled to reduce allocation during routing.

### Connection Count Limits
Kestrel connection limits should be configured to the expected maximum client count plus overhead. In .NET 10, `KestrelServerOptions.Limits.MaxConcurrentConnections` should be set explicitly.

---

## 11. Connector — Inbound Dispatch (Synthetic Client Connections)

### Problem

[§8](#8-connector--negotiate-interception) diverts negotiate so clients connect to Switchboard instead of the app server. This section covers the consequence: the app server now has **no real client connections**, yet `ChatHub.SendMessage(...)` must still run when a client invokes it.

`IHubLifetimeManager` does not solve this. It is **outbound only** — `Clients.All.SendAsync`, group and user targeting. Everything inbound (`OnConnectedAsync`, argument binding, hub filters, per-method `[Authorize]`, streaming, `OnDisconnectedAsync`) lives in `HubConnectionHandler<THub>` → `DefaultHubDispatcher`, and neither is reachable without a `ConnectionContext`.

### Mechanism — run the real pipeline over a synthetic connection

The Connector builds the standard SignalR connection pipeline once per hub, then invokes it with a `ConnectionContext` it fabricates per client. The framework cannot tell the difference: `HubConnectionHandler` reads bytes from `connection.Transport.Input` and writes to `connection.Transport.Output`, so a `Pipe` pair is a complete substitute for a socket.

```csharp
// Built once per hub type at startup.
ConnectionDelegate hubPipeline = new ConnectionBuilder(serviceProvider)
    .UseConnectionHandler<HubConnectionHandler<THub>>()
    .Build();
```

This is the same approach the Azure SignalR SDK takes — it "creates a `ConnectionContext` with the appropriate set of features and writes the message payload to the application's input pipe" ([azure-signalr#13](https://github.com/Azure/azure-signalr/issues/13)).

### The synthetic `ConnectionContext`

```csharp
internal sealed class SwitchboardClientConnectionContext : ConnectionContext
{
    // _toHub:   Connector writes  → HubConnectionHandler reads (connection.Transport.Input)
    // _fromHub: HubConnectionHandler writes → Connector reads   (connection.Transport.Output)
    private readonly Pipe _toHub   = new();
    private readonly Pipe _fromHub = new();

    public override string ConnectionId { get; set; }          // the service's client connectionId
    public override IFeatureCollection Features { get; } = new FeatureCollection();
    public override IDictionary<object, object?> Items { get; set; } = new ConnectionItems();
    public override IDuplexPipe Transport { get; set; }        // reader=_toHub, writer=_fromHub
}
```

Required features:

| Feature | Why |
|---|---|
| `IConnectionUserFeature` | **Load-bearing.** `HubConnectionContext` builds its caller context as `new DefaultHubCallerContext(this, _connectionContext.Features.Get<IConnectionUserFeature>()?.User ?? new ClaimsPrincipal())`. This is the *only* path by which identity reaches `Context.User`, `Context.UserIdentifier`, and per-method `[Authorize]`. |
| `IConnectionIdFeature` | `Context.ConnectionId` must equal the service's `connectionId` so `send_to_connection` round-trips. |
| `IConnectionItemsFeature` | `Context.Items` — apps rely on per-connection state. |
| `IConnectionHeartbeatFeature` | `HubConnectionHandler` registers keep-alive callbacks against it. |
| `IConnectionLifetimeFeature` | Exposes `ConnectionClosed` and `Abort()` to framework code that reads them via the feature rather than the base `ConnectionContext` properties directly. |
| `IConnectionCompleteFeature` | Lets framework code register a completion callback (`OnCompleted`) independent of pipeline teardown ordering. |

> **Confirmed empirically in the Phase 0 spike** ([09-phase0-findings/required-connection-features.md](09-phase0-findings/required-connection-features.md)): the original four-feature list above is necessary but not exhaustive — `IConnectionLifetimeFeature` and `IConnectionCompleteFeature` are also required on the synthetic context. None of the original four turned out to be unnecessary.

> **`Context.GetHttpContext()` returns `null`.** There is no `IHttpContextFeature` — no HTTP request exists on the app server for this connection. Hub code that reads headers, cookies, or `RemoteIpAddress` from `GetHttpContext()` will NPE. Document as a known incompatibility; any such data must be passed as claims through `open_connection` instead.

### Connection lifecycle

**`open_connection` → start the pipeline**

1. Reconstruct the `ClaimsPrincipal` (below) and set `IConnectionUserFeature`.
2. Start `hubPipeline(connectionContext)` on a background task; retain it for shutdown.
3. Synthesize the hub-protocol handshake into `_toHub` (below).
4. Register the context in a node-local `ConcurrentDictionary<string, SwitchboardClientConnectionContext>` keyed by `connectionId`.

**`client_message` → feed the pipe**

Write `envelope.Payload` **verbatim** into `_toHub.Writer` and flush. The payload is raw hub-protocol bytes *including* framing (`\x1e` for JSON, length-prefix for MessagePack), which is exactly what `IHubProtocol.TryParseMessage` expects — no transcoding, matching the raw-bytes passthrough contract in [Protocol Part 2](03-protocol.md#part-2-server-facing-protocol-app-server--service).

**`close_connection` → tear down**

Complete `_toHub.Writer`. `HubConnectionHandler` observes EOF, runs `OnDisconnectedAsync`, and completes the pipeline task. Remove from the dictionary.

### Handshake synthesis

`HubConnectionHandler` will not dispatch anything until a handshake completes — `if (!await connectionContext.HandshakeAsync(...)) { return; }` ([HubConnectionHandler.cs](https://github.com/dotnet/aspnetcore/blob/main/src/SignalR/server/Core/src/HubConnectionHandler.cs)). Since the *service* already performed the real handshake with the client, the Connector must replay it locally:

```csharp
HandshakeProtocol.WriteRequestMessage(
    new HandshakeRequestMessage(envelope.HubProtocol, 1), _toHub.Writer);
await _toHub.Writer.FlushAsync();
```

This requires the client's negotiated protocol at `open_connection` time. `ServerEnvelope` already carries `HubProtocol` at `[Key(5)]`, and the service knows the value by then (it sets `HubProtocol` during the handshake phase — see [§2](#2-client-connection-lifecycle)), so **`open_connection` populates `hubProtocol`**. No wire-format change; see [Protocol §2.3](03-protocol.md#23-message-envelope-format).

### Identity reconstruction

```csharp
var claims = new List<Claim>();
if (envelope.UserId is not null)
    claims.Add(new Claim(ClaimTypes.NameIdentifier, envelope.UserId));
foreach (var (type, value) in envelope.Claims ?? EmptyClaims)
    claims.Add(new Claim(type, value));

// authenticationType must be non-null for IsAuthenticated to be true -- but ONLY set it when
// there's an actual identity (userId) to assert. ClaimsIdentity.IsAuthenticated depends solely
// on authenticationType being non-null/non-empty, NOT on claim count -- an unconditional
// non-null authenticationType would silently authenticate anonymous connections too (confirmed
// empirically in the Phase 0 spike; see docs/docs/09-phase0-findings/inbound-dispatch-corrections.md).
var authenticationType = envelope.UserId is not null ? "Switchboard" : null;
var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType));
features.Set<IConnectionUserFeature>(new ConnectionUserFeature { User = principal });
```

The app server **trusts** these values. The trust boundary is the server connection itself, authenticated by `ServerSigningKey` ([Protocol §2.1](03-protocol.md#21-app-server-connection-establishment)); under Pattern B this merely round-trips the identity the app server itself supplied at negotiate.

> **`ClaimTypes.NameIdentifier` is synthesized from `userId` deliberately.** `Context.UserIdentifier` is computed by `IUserIdProvider`, whose default implementation reads `NameIdentifier` — while the service routes `send_to_user` using the `userId` it captured at negotiate. Seeding the claim from `userId` keeps the two definitions identical, so `Clients.User(id)` resolves to the same connections on both sides.
>
> **A custom `IUserIdProvider` breaks this silently.** If an app registers one that derives the id from a different claim, the app server's `Context.UserIdentifier` and the service's user index diverge, and user-targeted sends land nowhere. Apps with a custom provider must apply the same logic when supplying `userId` on the forwarded negotiate ([§8](#8-connector--negotiate-interception) step 2).

### Outbound from the pipeline

Two distinct return paths — do not conflate them:

| Hub API | Path |
|---|---|
| `Clients.All` / `.Group` / `.User` / `.Caller` / `.Client` | `IHubLifetimeManager` → `broadcast` / `send_to_group` / `send_to_user` / `send_to_connection` envelopes. **Never touches the pipe.** |
| `Completion`, `StreamItem`, hub-level `Close` | Written by `DefaultHubDispatcher` to `HubConnectionContext.Output` → `_fromHub`. |

So the Connector runs a read loop on `_fromHub.Reader`, parsing with the connection's `IHubProtocol`, and:

- **Drops** the handshake response (`{}`) — the service already sent the real one to the client.
- **Drops** `PingMessage` — the service owns client keep-alive ([§3 Health Monitoring](#3-server-connection-manager)); forwarding would double-ping.
- **Forwards** everything else — `StreamItem`, `Completion` (including a faulted stream's `HubException`), and hub-level `Close` alike — as a `send_to_connection` envelope for this `connectionId`, as the **exact original bytes** `TryParseMessage` consumed, never re-serialized.

> **Verified, not built (Phase 2 Slice 7, plan decision D12).** Server→client streaming (`StreamItem`/`Completion`), client→server streaming, `CancelInvocation`, and `Send`/`SendCore` (invocation with no `invocationId`, so no `Completion` is ever sent) all needed **no new mechanism** — they ride this same envelope path unchanged, across every transport and both hub protocols.
>
> **One real bug found and fixed: the outbound reader was lossy for MessagePack.** It originally parsed each outbound `HubMessage` with an `EmptyBinder` (`GetReturnType`/`GetStreamItemType` → `typeof(object)`, since the Connector doesn't know the hub method's real CLR return/stream-item types from raw bytes alone) and then **re-serialized** the parsed message before forwarding. For JSON this happened to survive by accident — the loosely-typed value round-trips through a `JsonElement` — but for MessagePack, arguments and stream items reconstructed from `object`-typed CLR values do not reliably re-encode to the same bytes. **Fix:** the reader now parses only to *classify* (Ping vs. Close vs. everything else) and forwards the exact span `TryParseMessage` consumed — captured as `before.Slice(0, before.Length - buffer.Length)` bracketing the parse call — never re-encoding anything. See [00-review-findings.md § Phase 2 Slice 7](00-review-findings.md).

### Rejection path

If the hub's `OnConnectedAsync` throws, `HubConnectionHandler` catches it, writes a close message, and returns without dispatching. **Confirmed in .NET 10 (Phase 0 spike):** the emitted frame is `{"type":7,"error":"Connection closed with an error."}` — there is **no `allowReconnect` field at all**, not `allowReconnect: false` as earlier assumed. Client libraries default a missing field to `false`, so behavior is unaffected, but implementations should not assume the field is present when parsing this frame. See [09-phase0-findings/inbound-dispatch-corrections.md](09-phase0-findings/inbound-dispatch-corrections.md). The Connector detects pipeline completion (or the close frame) and replies:

```json
{ "type": "close_connection", "connectionId": "<id>", "error": "<reason>" }
```

The service then closes the client transport with a SignalR `Close` frame. This reuses the existing envelope type — no schema addition. The tradeoff accepted: the client briefly observes a connected state before being closed, rather than paying a round-trip acknowledgement on every successful connect.

### Verified against upstream

Every framework claim above was checked against current ASP.NET Core source rather than inferred — the same discipline applied after the `NegotiateHandler` DI-override finding ([00-review-findings.md](00-review-findings.md)): `HubConnectionHandler<THub> : ConnectionHandler` (public, so `UseConnectionHandler<>` applies); the handshake gate; `Transport.Input.ReadAsync()` + `TryParseMessage`; `IConnectionUserFeature` as the sole identity path; class-level `[Authorize]` living in endpoint metadata, not the dispatcher.
