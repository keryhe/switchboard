# Data Models & State

This document defines the core data structures used throughout the service. These models are the shared language between all components.

---

## Client Connection State

Represents a connected client. Held in `IConnectionRegistry`.

```csharp
public sealed class ClientConnectionState
{
    // Identity
    public required string ConnectionId { get; init; }        // UUID from JWT connectionId claim; public identity (Context.ConnectionId)
    public required string ConnectionToken { get; init; }     // opaque transport handle (the `id` query param); distinct from ConnectionId
    public required string HubName { get; init; }             // e.g. "chatHub"
    public string? UserId { get; init; }                       // from JWT sub claim; null if anonymous

    // Transport & Protocol
    public required TransportType Transport { get; init; }    // WebSockets | ServerSentEvents | LongPolling
    public string? HubProtocol { get; set; }                   // "json" | "messagepack" — null until handshake completes
    public required IClientTransport TransportHandle { get; init; }

    // Routing
    public required string ServerConnectionId { get; set; }   // assigned physical server connection

    // Membership — ConcurrentDictionary<string, byte> used as a concurrent set (value is always 0)
    public ConcurrentDictionary<string, byte> Groups { get; } = new();

    // Timestamps
    public required DateTimeOffset ConnectedAt { get; init; }
    public DateTimeOffset LastSeen { get; set; }
}

public enum TransportType { WebSockets, ServerSentEvents, LongPolling }
```

> **`HubProtocol` is nullable:** It is `null` when the connection is first registered (accept phase) and set to `"json"` or `"messagepack"` after the hub handshake completes. No messages should be routed to a connection with `HubProtocol == null`. The registry's `SetProtocolAsync` method handles this update.

> **`ConcurrentHashSet<string>` does not exist in .NET.** `ConcurrentDictionary<string, byte>` with value `0` is the standard substitute. Use `TryAdd(key, 0)` to add, `TryRemove(key, out _)` to remove, and `.Keys` to enumerate.

> **`ConnectionToken` is distinct from `ConnectionId`.** `ConnectionId` is the public identity (used in `send_to_connection`, groups, management API, logs). `ConnectionToken` is an opaque, unguessable handle minted at the step-2 negotiate and presented by the client as the transport `id` query parameter; the service resolves it to the connection on the transport upgrade. Phase 1: a random value. Phase 3: encodes the owning node id so a transport request landing on any node can be routed to the owner without a registry lookup. Never expose `ConnectionToken` via the management API.

---

## Server Connection State

Represents a physical WebSocket connection from an app server to this service.

```csharp
public sealed class ServerConnectionState
{
    public required string ConnectionId { get; init; }        // UUID assigned at handshake
    public required string HubName { get; init; }
    public required string AppServerId { get; init; }         // identifier from server JWT

    // Routing
    public required IServerConnection Connection { get; init; }

    // Load tracking
    public int LogicalConnectionCount => _logicalCount;
    private int _logicalCount;
    public void IncrementLogicalCount() => Interlocked.Increment(ref _logicalCount);
    public void DecrementLogicalCount() => Interlocked.Decrement(ref _logicalCount);

    // Health
    public ServerConnectionStatus Status { get; set; } = ServerConnectionStatus.Connected;
    public DateTimeOffset ConnectedAt { get; init; }
    public DateTimeOffset LastPingSent { get; set; }
    public DateTimeOffset LastPongReceived { get; set; }
}

public enum ServerConnectionStatus { Connected, Degraded, Reconnecting, Disconnected }
```

---

## Hub Descriptor

A live snapshot of all connections for a given hub. Held in `IHubRegistry`.

```csharp
public sealed class HubDescriptor
{
    public required string HubName { get; init; }

    // All registered server connections for this hub (across all app servers)
    public ConcurrentDictionary<string, ServerConnectionState> ServerConnections { get; } = new();

    // Snapshot metrics (computed on read)
    public int ServerConnectionCount => ServerConnections.Count;
    public int ActiveServerConnectionCount => ServerConnections.Values.Count(s => s.Status == ServerConnectionStatus.Connected);
}
```

---

## Message Envelope (Service ↔ App Server)

The wire envelope wrapping a SignalR payload between the service and an app server. Encoded with **MessagePack**, length-prefixed (see [Protocol Part 2](03-protocol.md#part-2-server-facing-protocol-app-server--service)). Keys are assigned by `[Key(n)]` position, so field order is part of the wire contract — append new fields with new keys, never reuse or reorder.

```csharp
[MessagePackObject]
public sealed class ServerEnvelope
{
    [Key(0)]
    public required ServerEnvelopeType Type { get; init; }

    [Key(1)]
    public string? ConnectionId { get; init; }

    [Key(2)]
    public string? HubName { get; init; }

    [Key(3)]
    public string? GroupName { get; init; }

    [Key(4)]
    public string? UserId { get; init; }

    [Key(5)]
    public string? HubProtocol { get; init; }        // set on client_message/send_* AND on open_connection

    [Key(6)]
    public byte[]? Payload { get; init; }             // raw hub-protocol bytes (MessagePack bin); never base64

    [Key(7)]
    public IReadOnlyList<string>? ExcludedConnectionIds { get; init; }

    [Key(8)]
    public IReadOnlyDictionary<string, string>? Claims { get; init; }

    [Key(9)]
    public string? Error { get; init; }

    [Key(10)]
    public int? Version { get; init; }               // handshake protocol version (§2.2); added during Phase 1 implementation, additive — never reuse or reorder Key(0..9)
}

public enum ServerEnvelopeType
{
    Handshake,
    HandshakeAck,
    HandshakeError,
    OpenConnection,
    CloseConnection,
    ClientMessage,
    SendToConnection,
    Broadcast,
    SendToGroup,
    SendToUser,
    AddToGroup,
    RemoveFromGroup,
    Ping,
    Pong
}
```

---

## Negotiate Responses

Negotiate is a two-step redirect flow (see [Protocol §1.1](03-protocol.md#11-negotiation)), so `POST /{hub}/negotiate` has two response shapes.

**Step 1 — redirect response** (returned to the client via the app server). Identified by the presence of `url`:

```csharp
public sealed class RedirectResponse
{
    [JsonPropertyName("url")]
    public required string Url { get; init; }              // https:// URL of this service for the hub

    [JsonPropertyName("accessToken")]
    public required string AccessToken { get; init; }      // short-lived client JWT
}
```

**Step 2 — connection response** (returned when the client re-negotiates at the service `url` with the step-1 token):

```csharp
public sealed class NegotiateResponse
{
    [JsonPropertyName("connectionId")]
    public required string ConnectionId { get; init; }     // public identity

    [JsonPropertyName("connectionToken")]
    public required string ConnectionToken { get; init; }  // opaque transport handle (the `id` query param)

    [JsonPropertyName("negotiateVersion")]
    public required int NegotiateVersion { get; init; }    // 1

    [JsonPropertyName("availableTransports")]
    public required IReadOnlyList<AvailableTransport> AvailableTransports { get; init; }
}

public sealed class AvailableTransport
{
    [JsonPropertyName("transport")]
    public required string Transport { get; init; }       // "WebSockets" | "ServerSentEvents" | "LongPolling"

    [JsonPropertyName("transferFormats")]
    public required IReadOnlyList<string> TransferFormats { get; init; }  // "Text" | "Binary"
}
```

> **Step 2 wire serialization (Phase 1, resolved).** `Microsoft.AspNetCore.Http.Connections.NegotiateProtocol` is public (in `Http.Connections.Common.dll`) and its `NegotiationResponse` type already carries exactly the fields step 2 needs, serializing to precisely the shape this section specifies. The service uses `NegotiateProtocol.WriteResponse` directly for step 2 rather than hand-serializing `NegotiateResponse`/`AvailableTransport` above — free wire compatibility with the framework's own client-side parser. The redirect (step 1) response stays hand-written as `RedirectResponse`, because `NegotiationResponse`'s own JSON output for a redirect-shaped payload carries two extra fields (`negotiateVersion`, `availableTransports: []`) that §1.1 says a redirect must not have.

---

## Group Membership

Maintained separately from `ClientConnectionState` because group membership needs to be queried from both directions (connection→groups, group→connections).

**In-memory:**
```csharp
// group key: "{hubName}::{groupName}" → set of connectionIds
// ConcurrentDictionary<string, byte> used as concurrent set (value always 0)
ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _groupToConnections;
// connection key: connectionId → set of groupNames
ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _connectionToGroups;
```

**Orleans grains (Phase 3):**

Group membership lives entirely in `IGroupGrain` state — the grain holds the set of `connectionId` values for the group and handles fan-out directly. **`IGroupGrain` is the single source of truth under clustering**; `InMemoryConnectionRegistry` is not in play (`OrleansConnectionRegistry` replaces it wholesale — see [ADR-002](07-adr/ADR-002-connection-registry.md)).

Each node does keep a **node-local cache** mapping `connectionId → groupNames` for its own connections, held alongside the `IClientTransport` handles in `ILocalTransportRegistry`. Its sole purpose is disconnect cleanup: the node can call `IGroupGrain.RemoveAsync(connectionId)` for exactly the groups that connection belonged to, instead of scanning every group grain. It is never read for routing or membership queries, and is discarded with the connection.

---

## Backplane Delivery (Grain Observer Arguments)

Cross-node delivery uses Orleans **grain observers**, not a serialized message envelope. When a hub grain fans out to other nodes, it invokes the `IHubObserver` methods on each registered `HubObserverImpl`, passing the payload and routing fields directly as method arguments (see [Design §7](04-design.md#7-backplane-design-phase-3) for the `IHubObserver` interface):

```csharp
void OnBroadcast(byte[] payload, string hubProtocol, string[] excludedConnectionIds);
void OnGroupMessage(string groupName, byte[] payload, string hubProtocol, string[] excludedConnectionIds);
void OnUserMessage(string userId, byte[] payload, string hubProtocol);
void OnConnectionMessage(string connectionId, byte[] payload, string hubProtocol);
```

> **Orleans serialization at the observer boundary.** Grain-observer method arguments must be Orleans-serializable. `byte[]`, `string`, and `string[]` are natively serializable, so no `[GenerateSerializer]` DTO is required for backplane delivery. `ReadOnlyMemory<byte>` is **not** Orleans-serializable — convert to `byte[]` at the grain/observer boundary and back to `ReadOnlyMemory<byte>` on the local side before writing to transports.

---

## Configuration Models

```csharp
public sealed class SwitchboardOptions
{
    // --- Network ---
    // The URL that clients use to reach this service (used in negotiate redirect responses).
    // Must be set to the public/external address in any deployment where the bind address
    // differs from the reachable address (behind a reverse proxy, load balancer, etc.)
    public required string PublicUrl { get; set; }              // e.g. "wss://signalr.mycompany.com"

    // CORS — origins allowed to call negotiate and connect (required for browser clients)
    public string[] AllowedOrigins { get; set; } = [];          // e.g. ["https://app.mycompany.com"]

    // --- Pattern A: service-direct negotiate (optional; see 04-design.md §1) ---
    // When true, TrustedProxyNetworks MUST be non-empty or the service refuses to start.
    // Identity headers are honoured only from allowlisted peers and stripped otherwise.
    public bool EnableDirectNegotiate { get; set; } = false;
    public string TrustedIdentityHeader { get; set; } = "X-Switchboard-UserId";
    public string TrustedClaimsHeader { get; set; } = "X-Switchboard-Claims";
    public string[] TrustedProxyNetworks { get; set; } = [];    // CIDR, e.g. ["10.0.0.0/8"]

    // --- Client JWT (short-lived tokens issued to clients at negotiate time) ---
    public required string TokenSigningKey { get; set; }        // HMAC-SHA256 secret (min 32 chars)
    public string TokenIssuer { get; set; } = "switchboard";
    public string TokenAudience { get; set; } = "switchboard-client";
    public TimeSpan ClientTokenExpiry { get; set; } = TimeSpan.FromSeconds(60);

    // --- Server JWT (long-lived tokens used by app servers to authenticate) ---
    public required string ServerSigningKey { get; set; }       // separate secret from TokenSigningKey
    public string? ServerSigningKeyFallback { get; set; }       // previous key, kept during rotation window

    // --- Management JWT (long-lived tokens used by admin tools and operators) ---
    // Third, independent secret. Never reuse TokenSigningKey or ServerSigningKey here:
    // an app server token must not be able to drive the management API.
    public string? ManagementSigningKey { get; set; }           // required when the management API is enabled (Phase 4)
    public string? ManagementSigningKeyFallback { get; set; }   // previous key, kept during rotation window
    public string ManagementAudience { get; set; } = "switchboard-management";

    // --- Server connections ---
    public int MinServerConnectionsPerHub { get; set; } = 5;
    public TimeSpan ServerPingInterval { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan ServerPingTimeout { get; set; } = TimeSpan.FromSeconds(5);

    // --- Client connections ---
    public TimeSpan ClientKeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan ClientHandshakeTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public int MaxClientConnectionsPerHub { get; set; } = 0;   // 0 = unlimited

    // --- Write channel ---
    public int WriteChannelCapacity { get; set; } = 256;
    // DropWrite, not Wait: a slow client must never stall broadcast fan-out for everyone
    // else. When the channel is full the incoming message is dropped and the connection
    // is flagged — see 04-design.md §5.
    public BoundedChannelFullMode WriteChannelFullMode { get; set; } = BoundedChannelFullMode.DropWrite;

    // --- Orleans / Clustering (Phase 3) ---
    public bool UseOrleansCluster { get; set; } = false;        // false = single-node in-memory silo
    public string? OrleansAdoNetConnectionString { get; set; }  // SQL Server or PostgreSQL connection string
    public string? OrleansAdoNetInvariant { get; set; }         // "System.Data.SqlClient" or "Npgsql"
    public string OrleansClusterId { get; set; } = "switchboard";
    public string OrleansServiceId { get; set; } = "switchboard";
}
```

> **`PublicUrl` is required.** The negotiate response `url` field is constructed from this value. Setting it incorrectly causes clients to attempt WebSocket connections to an unreachable address. In local development, set it to `ws://localhost:7000`. Behind a reverse proxy, set it to the proxy's external HTTPS/WSS address.

---

## Service Registration Summary

```
IConnectionRegistry        → InMemoryConnectionRegistry   (Phase 1)
                             OrleansConnectionRegistry     (Phase 3, UseOrleansCluster = true)

ILocalTransportRegistry    → LocalTransportRegistry       (singleton, all phases — holds IClientTransport handles)

IHubRegistry               → InMemoryHubRegistry

IServerConnectionSelector  → RoundRobinServerConnectionSelector

IMessageRouter             → DefaultMessageRouter

INegotiationService        → DefaultNegotiationService

ITokenService              → JwtTokenService

IBackplane                 → NoOpBackplane                 (Phase 1)
                             OrleansObserverBackplane      (Phase 3, UseOrleansCluster = true)

IGrainFactory              → (provided by Orleans silo host)
```

All registrations go through ASP.NET Core's `IServiceCollection` DI container, configured via `builder.Services.AddSwitchboard(options => { ... })`. The Orleans silo is co-hosted using `IHostedService` with `ISiloBuilder`.

## Orleans Grain Interfaces Summary

Defined in `Keryhe.Switchboard.Orleans`. All implement `IGrainWithStringKey`.

```csharp
IHubGrain          // key: hubName — connection registry + observer fan-out coordinator
IGroupGrain        // key: "hubName::groupName" — group membership (connectionId + nodeId pairs)
IUserGrain         // key: "hubName::userId" — user connection set (connectionId + nodeId pairs)
IConnectionGrain   // key: connectionId — maps connectionId to owning nodeId
```

`IHubObserver` is an `IGrainObserver` (not a grain) implemented by `HubObserverImpl` — a plain class registered per silo that routes backplane messages to local transports via `ILocalTransportRegistry`.

See [Design doc — Section 7](04-design.md#7-backplane-design-phase-3) for full method signatures.
