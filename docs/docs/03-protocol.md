# Protocol Specification

This document specifies the wire protocols used between (1) clients and the service, and (2) app servers and the service. Existing SignalR client libraries (JavaScript, .NET, Java, etc.) connect to this service using the standard SignalR client protocol — no client changes required.

---

## Part 1: Client-Facing Protocol

This is the standard ASP.NET Core SignalR client protocol. The service is fully compatible with existing SignalR client libraries.

### 1.1 Negotiation

Negotiation is a **two-step redirect flow**, identical to the standard ASP.NET Core SignalR redirect (the same flow the Azure SignalR SDK uses). The client first negotiates with the app server, which returns a redirect response pointing at this service; the client then negotiates a second time directly with this service to obtain its connection token and transport list, and only then opens a transport.

**Step 1 — Client negotiates with the app server**

Request:
```
POST /{hubName}/negotiate?negotiateVersion=1
Authorization: Bearer <optional app-level token>
Content-Type: application/json
```

Redirect response:
```json
{
  "url": "https://switchboard.internal/{hubName}",
  "accessToken": "<short-lived JWT>"
}
```

A redirect response is identified by the presence of `url` and carries **only** `url` and `accessToken`; `availableTransports` and the connection identifiers come from step 2. `url` is an **`https://`** URL (not `wss://`) — the client derives the WebSocket scheme itself when it opens the transport.

The `accessToken` encodes:
- `connectionId` — UUID assigned to this connection
- `hubName` — the hub the client is connecting to
- `sub` — userId if the app server authenticated the user
- `exp` — short TTL (30–60 seconds; long enough to complete step 2 and the transport upgrade)

**Step 2 — Client negotiates with this service**

The client repeats negotiate against the redirect `url`, presenting the access token from step 1:
```
POST /{hubName}/negotiate?negotiateVersion=1
Authorization: Bearer <accessToken from step 1>
```

This service validates the token and returns a standard (non-redirect) negotiate response:
```json
{
  "connectionId": "<connectionId>",
  "connectionToken": "<connectionToken>",
  "negotiateVersion": 1,
  "availableTransports": [
    { "transport": "WebSockets", "transferFormats": ["Text", "Binary"] },
    { "transport": "ServerSentEvents", "transferFormats": ["Text"] },
    { "transport": "LongPolling", "transferFormats": ["Text", "Binary"] }
  ]
}
```

> **`connectionToken` vs `connectionId` — distinct values.** `connectionId` is the **public identity** surfaced to hub code (`Context.ConnectionId`) and used in `send_to_connection`, group membership, and the management API. `connectionToken` is an **opaque, unguessable handle** minted by this service at step 2 and used only as the transport `id` query parameter; the service maps it back to the connection on the transport upgrade. It stays a plain random value in every deployment mode, including clustered (Phase 3) — the token itself never encodes the owning node. Instead, for the two transports that need it (SSE and Long Polling, whose lifecycle spans multiple independent HTTP requests — plan decision D19), a request landing on a non-owning node resolves ownership through `IConnectionTokenOwnerGrain` (keyed by the token) and forwards internally to the owner; WebSocket needs no such lookup, since its entire lifecycle is one request. Clients treat `connectionToken` as opaque and never parse it. The token is never returned by the management API.

**Error Responses** (either step)
- `401 Unauthorized` — missing or invalid auth (app-level token in step 1, or the access token in step 2)
- `403 Forbidden` — authenticated but not allowed
- `503 Service Unavailable` — no app servers registered for this hub

---

### 1.2 WebSocket Transport

After step 2 of negotiation, the client upgrades to WebSocket against the redirect `url` (with the scheme switched to `wss`):

```
GET /{hubName}?id={connectionToken}&access_token={JWT}
Upgrade: websocket
```

The `id` query parameter is the `connectionToken` from the step-2 negotiate response. The `access_token` query parameter is the JWT from step 1 (standard SignalR convention for environments that cannot set Authorization headers on WebSocket upgrades).

**Hub Protocol Handshake**

Immediately after the WebSocket connection is established, both sides exchange a handshake before any hub messages flow:

Client sends (JSON protocol example):
```json
{"protocol":"json","version":1}
```
Followed by ASCII record separator `\x1e`.

Service responds with success:
```json
{}
```
Followed by `\x1e`.

Or with error:
```json
{"error":"Requested protocol 'xyz' is not available."}
```

Supported protocols: `json` (version 1), `messagepack` (version 1).

The handshake request and response are **always JSON**, regardless of which hub protocol is being
negotiated — a MessagePack client sends its (JSON) handshake request inside a *binary* WebSocket
frame, and receives a JSON handshake response the same way. Only the WebSocket frame *type* the
handshake and every subsequent message travel in follows the negotiated protocol's transfer format
(`Text` for `json`, `Binary` for `messagepack`) — the handshake bytes themselves never vary.

---

### 1.3 Message Framing

**JSON Hub Protocol**  
Messages are delimited by the ASCII record separator character `\x1e` (0x1E). A single WebSocket text frame may contain multiple messages separated by `\x1e`.

```
{"type":1,"invocationId":"1","target":"SendMessage","arguments":["hello"]}\x1e
```

**MessagePack Hub Protocol**  
Messages are length-prefixed using the same variable-length integer encoding SignalR's own
MessagePack protocol uses: 7 bits of value per byte, **least-significant group first**, with the
high bit of each byte set as a continuation flag (clear on the final byte). The prefix's decoded
value is the byte length of the following MessagePack-encoded message array. A single WebSocket
binary frame may contain multiple length-prefixed messages back to back.

---

### 1.4 Hub Message Types

| Type ID | Name | Direction | Description |
|---|---|---|---|
| 1 | `Invocation` | Client↔Server | Call a hub method |
| 2 | `StreamItem` | Server→Client | One item in a streaming response |
| 3 | `Completion` | Server→Client | Final result or error for an invocation |
| 4 | `StreamInvocation` | Client→Server | Initiate a server-side streaming call |
| 5 | `CancelInvocation` | Client→Server | Cancel an in-progress streaming call |
| 6 | `Ping` | Either | Keep-alive heartbeat |
| 7 | `Close` | Server→Client | Graceful connection close |

**Invocation (type 1)**
```json
{
  "type": 1,
  "headers": {},
  "invocationId": "1",
  "target": "SendMessage",
  "arguments": ["Alice", "Hello world"],
  "streamIds": []
}
```

**Completion (type 3)**
```json
{
  "type": 3,
  "invocationId": "1",
  "result": null
}
```
Or with error:
```json
{
  "type": 3,
  "invocationId": "1",
  "error": "Hub method threw an exception"
}
```

**Ping (type 6)**
```json
{"type":6}
```

**Close (type 7)**
```json
{"type":7,"error":null,"allowReconnect":true}
```

---

### 1.5 Server-Sent Events Transport

Client connects via:
```
GET /{hubName}?id={connectionToken}&access_token={JWT}
Accept: text/event-stream
```

The service must flush the response headers to the socket explicitly (`FlushAsync` after starting
the response) rather than relying on the framework to do it implicitly — the real client's
transport blocks on `HttpCompletionOption.ResponseHeadersRead` and hangs indefinitely if headers
are only logically committed but never actually written to the wire.

Service streams events, one hub message's raw bytes per `data:` line, each event terminated with
**CRLF CRLF** (`\r\n\r\n`), not a bare `\n\n`. The .NET 10 SignalR client's SSE parser tolerates
LF-only termination, but the .NET 8 client's does not — it throws
`FormatException: Unexpected '\n' in message` and never completes the handshake. CRLF is what
every SSE consumer accepts, so it is the wire format, not an optional accommodation:
```
data: {"type":6}\x1e\r\n
\r\n
data: {"type":1,"target":"ReceiveMessage","arguments":["Bob","Hi"]}\x1e\r\n
\r\n
```

Client sends messages via HTTP POST:
```
POST /{hubName}?id={connectionToken}&access_token={JWT}
Content-Type: application/json; charset=utf-8

{"type":1,"invocationId":"2","target":"SendMessage","arguments":["hello"]}\x1e
```

SSE is **Text-only** — negotiate advertises `ServerSentEvents` with `transferFormats: ["Text"]`,
never `Binary`, so a client cannot negotiate `messagepack` over this transport (handshake
negotiation rejects it explicitly rather than silently corrupting binary data through a
text-oriented stream). Use WebSocket or Long Polling for MessagePack.

---

### 1.6 Long Polling Transport

**Poll (client waits for server messages):**
```
GET /{hubName}?id={connectionToken}&access_token={JWT}
```
Response: **200** with accumulated messages if any arrived, or **200 with an empty body** if the poll
simply timed out with nothing to report — the real client's own long-polling transport treats
*any* `204` as "this connection is over" and stops polling for good, so a plain idle timeout must
never produce one (verified against the real `Microsoft.AspNetCore.Http.Connections.Client`/
`Microsoft.AspNetCore.Http.Connections` implementations; see
[00-review-findings.md § Phase 2 Slices 5-6](00-review-findings.md)). **204 No Content** is
reserved for when the connection has actually ended (DELETE received, or the service's own idle
reaper gave up on it after `DisconnectTimeout`) and there's nothing left buffered to return. The
very first GET for a given `connectionToken` is special: it establishes the connection and returns
immediately (empty 200), before any handshake or message has happened yet — the real client's
`LongPollingTransport.StartAsync` only checks for a success status on this call and never reads
its body.

**Send (client sends messages):**
```
POST /{hubName}?id={connectionToken}&access_token={JWT}
Content-Type: application/json; charset=utf-8

{message}\x1e
```

**Delete (client disconnect):**
```
DELETE /{hubName}?id={connectionToken}&access_token={JWT}
```

---

## Part 2: Server-Facing Protocol (App Server ↔ Service)

App servers connect to the service using a persistent WebSocket connection. The service-to-server protocol is a lightweight envelope format layered on top of the standard SignalR hub protocol.

> **Wire format: MessagePack, length-prefixed.** Every frame on the server-facing connection — the handshake (§2.2) and all message envelopes (§2.3) — is a MessagePack-encoded [`ServerEnvelope`](05-data-models.md#message-envelope-service--app-server), length-prefixed (the prefix is the byte length of the following MessagePack payload, matching the client-side MessagePack framing in §1.3). The `payload` field carries the inner SignalR hub message as **raw bytes** (MessagePack `bin`) — never base64. The JSON objects shown below illustrate each envelope's *logical fields* for readability; on the wire they are compact MessagePack encoded by the `[Key(n)]` order defined on `ServerEnvelope`. This mirrors the Azure SignalR server-service protocol and avoids the ~33% base64 inflation a JSON envelope would impose, especially on MessagePack clients.

### 2.1 App Server Connection Establishment

App servers connect to:
```
wss://switchboard.internal/server/{hubName}
Authorization: Bearer <server access token>
```

**Server Access Token Specification**

The server access token is a long-lived JWT that authenticates the app server to the service. It is signed with a dedicated `ServerSigningKey` — a separate secret from the `TokenSigningKey` used for client tokens — so the two token types cannot be confused or substituted.

Required claims:

```json
{
  "sub": "chat-api-server-01",
  "role": "appserver",
  "hubs": ["chatHub", "notificationHub"],
  "iss": "switchboard",
  "aud": "switchboard-server",
  "iat": 1700000000,
  "nbf": 1700000000,
  "exp": 1700086400
}
```

| Claim | Description |
|---|---|
| `sub` | A stable identifier for this app server instance (e.g. service name + hostname). Used in logs and metrics. |
| `role` | Must be the literal string `"appserver"`. The service rejects tokens without this role. |
| `hubs` | Array of hub names this server is authorized to register connections for. The service rejects connections for hubs not listed. |
| `iss` / `aud` | Must match `SwitchboardOptions.TokenIssuer` and the literal `"switchboard-server"` respectively. |
| `exp` | Recommended TTL: 24 hours. The Connector library should refresh the token and reconnect before expiry. |

**Token Generation**

Server tokens are pre-generated using a CLI tool provided by `Keryhe.Switchboard.Server`:

```
dotnet switchboard token generate \
  --server-id chat-api-server-01 \
  --hubs chatHub,notificationHub \
  --ttl 24h \
  --key <ServerSigningKey>
```

The generated token is stored in the app server's configuration (e.g. environment variable, secrets manager). It is not generated at runtime.

**Rotation**

The service supports two valid `ServerSigningKey` values simultaneously via `ServerSigningKeyFallback` in config. To rotate:
1. Add the new key as `ServerSigningKeyFallback`
2. Regenerate all server tokens signed with the new key
3. Deploy updated tokens to all app servers
4. Promote the new key to `ServerSigningKey`, clear `ServerSigningKeyFallback`

---

### 2.2 Server Handshake

Immediately after the WebSocket upgrade, the app server sends a handshake request (a `ServerEnvelope` of type `Handshake`, MessagePack-encoded and length-prefixed like every other frame — see the wire-format note above):

```json
{
  "type": "handshake",
  "version": 1,
  "hubName": "chatHub"
}
```

Service responds:
```json
{
  "type": "handshake_ack",
  "connectionId": "<server-connection-uuid>"
}
```

On version mismatch:
```json
{
  "type": "handshake_error",
  "error": "Unsupported protocol version: 2"
}
```

---

### 2.3 Message Envelope Format

All messages between app server and service are wrapped in a MessagePack `ServerEnvelope`, length-prefixed (see the wire-format note at the start of Part 2). The examples below show logical fields; `payload` is raw hub-protocol bytes, not base64.

**Service → App Server (forwarding a client message):**
```json
{
  "type": "client_message",
  "connectionId": "<client-connection-id>",
  "hubProtocol": "json",
  "payload": "<raw hub-protocol message bytes>"
}
```

**Service → App Server (client connected notification):**
```json
{
  "type": "open_connection",
  "connectionId": "<client-connection-id>",
  "hubProtocol": "json",
  "userId": "alice",
  "claims": { "role": "admin" }
}
```

> **`hubProtocol` is required on `open_connection`.** The service completes the hub-protocol handshake with the client before emitting this envelope, so the value is known. The Connector needs it to synthesize a handshake for the app server's synthetic connection — `HubConnectionHandler` dispatches nothing until one completes. See [04-design.md §11](04-design.md#11-connector--inbound-dispatch-synthetic-client-connections).

**Service → App Server (client disconnected notification):**
```json
{
  "type": "close_connection",
  "connectionId": "<client-connection-id>",
  "error": null
}
```

**App Server → Service (reject or terminate a connection):**

`close_connection` is bidirectional. The app server sends it when the hub refuses or drops a connection — most commonly when `OnConnectedAsync` throws — and the service responds by closing the client transport with a SignalR `Close` frame:

```json
{
  "type": "close_connection",
  "connectionId": "<client-connection-id>",
  "error": "Hub rejected the connection"
}
```

There is no `open_connection` acknowledgement: a connection is assumed accepted unless the app server says otherwise. The client therefore briefly observes a connected state before a rejection closes it — the accepted cost of avoiding a round-trip on every successful connect.

**App Server → Service (send to specific client):**
```json
{
  "type": "send_to_connection",
  "connectionId": "<client-connection-id>",
  "hubProtocol": "json",
  "payload": "<raw hub-protocol message bytes>"
}
```
A targeted send has exactly one recipient, whose negotiated protocol the service already knows —
so `send_to_connection` only ever carries a single `payload` encoded for that recipient. It never
carries `payloads` (see below); there is nothing mixed-protocol about a single connection.

**App Server → Service (broadcast to all clients in hub / send to group / send to user — mixed-protocol fan-out, `[Key(11)] payloads`):**

`broadcast`, `send_to_group`, and `send_to_user` can each fan out to a mix of `json` and
`messagepack` recipients, and the app server doesn't know each recipient's protocol in advance. Per
`[Key(11)] payloads` on `ServerEnvelope` — a `Dictionary<string, byte[]>` keyed by protocol name
(`"json"` / `"messagepack"`) — the Connector always populates **both** encodings, and the service's
router picks whichever entry matches each recipient's own negotiated protocol. `payload` /
`hubProtocol` (singular) are still set alongside `payloads`, holding the `"json"` encoding, purely
so an envelope inspected without `payloads`-awareness (e.g. an older reader) still gets something
usable — the router itself always prefers `payloads` when present.
```json
{
  "type": "broadcast",
  "hubName": "chatHub",
  "hubProtocol": "json",
  "payload": "<raw json-encoded hub-protocol message bytes>",
  "payloads": {
    "json": "<raw json-encoded hub-protocol message bytes>",
    "messagepack": "<raw messagepack-encoded hub-protocol message bytes>"
  },
  "excludedConnectionIds": []
}
```
```json
{
  "type": "send_to_group",
  "groupName": "room-42",
  "hubProtocol": "json",
  "payload": "<raw json-encoded hub-protocol message bytes>",
  "payloads": { "json": "<...>", "messagepack": "<...>" },
  "excludedConnectionIds": []
}
```
```json
{
  "type": "send_to_user",
  "userId": "alice",
  "hubProtocol": "json",
  "payload": "<raw json-encoded hub-protocol message bytes>",
  "payloads": { "json": "<...>", "messagepack": "<...>" }
}
```

**App Server → Service (group membership):**
```json
{ "type": "add_to_group", "connectionId": "<id>", "groupName": "room-42" }
{ "type": "remove_from_group", "connectionId": "<id>", "groupName": "room-42" }
```

**Ping / Keep-alive:**
```json
{ "type": "ping" }
```
Expected response:
```json
{ "type": "pong" }
```

---

### 2.4 Connection Multiplexing

A single physical WebSocket connection between an app server and the service carries traffic for many logical client connections. The `connectionId` field in each envelope identifies which client the message is for or from.

The service assigns clients to server connections based on a load-distribution policy (default: round-robin across available server connections for the hub). Once a client is assigned to a server connection, all messages for that client flow over that same server connection for the lifetime of the client connection.

> **Ordering requirement.** `open_connection` for a client MUST be written to that client's assigned server connection before any `client_message` for the same client, and every subsequent `client_message` MUST follow on that same connection, in order. The Connector's inbound dispatch ([04-design.md §11](04-design.md#11-connector--inbound-dispatch-synthetic-client-connections)) relies on this — there is deliberately no acknowledgement of `open_connection`, so a client invoking a hub method immediately after connecting is only safe because the single writer per server connection, plus TCP ordering, guarantees `open_connection` is processed first. This requirement is single-writer-per-connection, not "one envelope in flight at a time" — implementations must not write a given client's envelopes to two different server connections during its lifetime.

---

### 2.5 Protocol Version Negotiation

The handshake `version` field allows future protocol evolution:

| Version | Features |
|---|---|
| 1 | Core message routing, broadcast, groups, users, multiplexing |
| 2 | (reserved) Binary payload encoding, streaming improvements |

If the service does not support the requested version, it responds with `handshake_error` and closes the connection. App servers should fall back to the highest mutually supported version.

---

## Part 3: Management REST API

The management API uses HTTP/JSON and is authenticated with a **management access token** — a third token type, distinct from both client and server tokens and signed with its own `ManagementSigningKey`. A server access token is **not** accepted here: app servers must not be able to drive the management API. A server access token presented at `/api/v1` gets **`401 Unauthorized`, not `403 Forbidden`** — it fails validation at the audience and signing key before role is ever considered, so it is cryptographically indistinguishable from a garbage token; synthesizing a nicer 403 would mean validating it a second time purely to produce a different status code. See [ADR-004](07-adr/ADR-004-token-authority.md).

The entire `/api/v1` route group is additionally gated by `ManagementAllowedNetworks` (default empty = no restriction beyond the token) — a CIDR allowlist evaluated against the caller's remote IP, using the same matcher and IPv4-mapped-IPv6 normalization as `TrustedProxyNetworks` (§1). This is defence in depth on top of the token, not a replacement for it: the management API is mapped onto the same Kestrel listener as ordinary client traffic (Phase 4 plan decision D21) rather than a separate port, so a single leaked or long-lived token would otherwise reach an admin surface from anywhere. A request outside the allowlist gets `403 Forbidden`, checked before the token itself.

If `EnableManagementApi` is `false` or `ManagementSigningKey` is not configured, every route under `/api/v1` is **not mapped at all** — a `404 Not Found`, not a `401`. This fails closed by absence rather than by an unauthenticated surface answering with the wrong status.

**Base URL:** `https://switchboard.internal/api/v1`

**Authentication:**
```
Authorization: Bearer <management access token>
```

Required claims:

```json
{
  "sub": "ops-dashboard",
  "role": "management",
  "iss": "switchboard",
  "aud": "switchboard-management",
  "iat": 1700000000,
  "exp": 1700086400
}
```

| Claim | Description |
|---|---|
| `sub` | Stable identifier for the calling tool or operator. Used in audit logs. |
| `role` | Must be the literal string `"management"`. Tokens carrying `"appserver"` are rejected. |
| `iss` / `aud` | Must match `SwitchboardOptions.TokenIssuer` and `SwitchboardOptions.ManagementAudience` (default `"switchboard-management"`). |
| `exp` | Recommended TTL: 24 hours. Rotation follows the same two-key procedure as server tokens (§2.1), using `ManagementSigningKeyFallback`. |

Management tokens are generated with the same CLI, selecting the management role:

```
dotnet switchboard token generate \
  --role management \
  --subject ops-dashboard \
  --ttl 24h \
  --key <ManagementSigningKey>
```

---

### Broadcast to Hub

```
POST /api/v1/hubs/{hubName}/send
Content-Type: application/json

{
  "target": "ReceiveMessage",
  "arguments": ["System", "Server is restarting in 5 minutes"]
}
```
Response: `202 Accepted`, including when the hub has no connections at all — fan-out to zero targets is not an error.

**Argument encoding.** `arguments` is a plain JSON array; each element is mapped to a hub-protocol primitive (`string` / `long` / `double` / `bool` / `null` / nested array / nested object) before being encoded for whichever protocol each recipient negotiated (JSON or MessagePack) — this mapping is load-bearing: an implementation that hands `JsonElement`s straight to a MessagePack encoder without this step produces silently-empty argument maps for every MessagePack client, with no exception anywhere.

**Management sends bypass app servers and hub code entirely.** This endpoint (and Send to User / Send to Group below) constructs the invocation and injects it straight into fan-out — no `IHubContext`, no hub method invocation, no app server round trip. If your hub has a method named `ReceiveMessage`, it does **not** run; the client's own handler for that method name is what receives this call, exactly as if a real hub method had invoked it. This matches Azure SignalR Service's REST API semantics.

---

### Send to User

```
POST /api/v1/hubs/{hubName}/users/{userId}/send
Content-Type: application/json

{
  "target": "PrivateMessage",
  "arguments": ["You have a new notification"]
}
```
Response: `202 Accepted`

---

### Send to Group

```
POST /api/v1/hubs/{hubName}/groups/{groupName}/send
Content-Type: application/json

{
  "target": "GroupMessage",
  "arguments": ["Hello group"]
}
```
Response: `202 Accepted`

---

### Add Connection to Group

```
PUT /api/v1/hubs/{hubName}/groups/{groupName}/connections/{connectionId}
```
Response: `200 OK`

---

### Remove Connection from Group

```
DELETE /api/v1/hubs/{hubName}/groups/{groupName}/connections/{connectionId}
```
Response: `200 OK`

---

### List Active Connections

```
GET /api/v1/hubs/{hubName}/connections?limit={n}&continuationToken={token}
```

**Mandatorily paginated** — a cluster-wide listing is, by construction, one grain call per connection, so an unbounded version of this endpoint is a self-inflicted outage on the first cluster with real production traffic. `limit` defaults to 100 and is clamped to a hard maximum of 1000; `continuationToken` is opaque (an implementation detail — do not parse it) and absent from the response once the last page has been returned.

Response:
```json
{
  "connections": [
    {
      "connectionId": "abc-123",
      "userId": "alice",
      "transport": "WebSockets",
      "connectedAt": "2025-10-01T12:34:56Z",
      "groups": ["room-42"]
    }
  ],
  "totalCount": 1,
  "nextContinuationToken": null
}
```

`totalCount` is the hub's full cluster-wide membership count (one call), not the size of the returned page — a client paging through results should stop once `nextContinuationToken` is absent, not once it has seen `totalCount` items on any single page.

---

### Health Check

Two endpoints, split so the public probe reveals no topology.

**Liveness / readiness — public, unauthenticated.** For load-balancer and orchestrator probes:
```
GET /healthz
```
- `200 OK` when the service is ready to accept connections (in clustered mode: the silo is active and at least one server connection exists for every registered hub)
- `503 Service Unavailable` otherwise

The body is intentionally minimal and exposes no counts or hub names:
```json
{ "status": "healthy" }
```

**Detailed health — authenticated.** Behind the management API (same bearer token as the other management endpoints); returns per-hub connection detail for operators and dashboards:
```
GET /api/v1/health
Authorization: Bearer <management access token>
```
Response: `200 OK`
```json
{
  "status": "healthy",
  "serverConnections": { "chatHub": 5 },
  "clientConnections": 1024
}
```

Both counts are cluster-wide in every deployment mode: in single-node/in-memory mode the node simply *is* the whole cluster, and in clustered mode a hub known only to a different node still appears here, backed by a small cluster-wide hub/node directory (`IClusterInventory`) rather than this node's own local state. Unlike `/healthz`, this endpoint may do real cluster I/O — it is an authenticated operator endpoint called at human rates, not a load-balancer probe, and must never acquire a load-balancer caller.
