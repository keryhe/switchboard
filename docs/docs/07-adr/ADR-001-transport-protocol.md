# ADR-001: WebSocket for App Server Connections (vs. gRPC)

**Status:** Accepted  
**Date:** 2025-10

---

## Context

App servers must maintain persistent connections to the SignalR Service to send and receive messages for their connected clients. Two realistic options exist for this physical connection:

1. **WebSocket** — raw full-duplex byte stream with a simple length/delimiter framing layer on top
2. **gRPC** — structured RPC over HTTP/2 with bidirectional streaming, strongly typed contracts via protobuf

---

## Decision

Use **WebSocket** for app server-to-service connections.

---

## Rationale

**Alignment with existing ecosystem.** Azure SignalR Service uses WebSocket for server connections, and the open-source `azure-signalr` SDK demonstrates this. Matching the transport makes it easier to write a compatible connector library and easier for developers familiar with Azure SignalR Service to reason about this system.

**Simplicity.** The server-facing protocol is a simple envelope format (type + connectionId + payload). gRPC's code generation, protobuf schema management, and streaming semantics add complexity without meaningful benefit at this protocol complexity level.

**Multiplexing control.** With WebSocket, the service controls exactly how logical client connections are multiplexed over physical server connections. gRPC's HTTP/2 stream multiplexing is handled by the HTTP/2 layer and is harder to tune for this specific workload.

**No strong typing requirement.** The payload inside envelopes is already typed by the SignalR hub protocol (JSON or MessagePack). Adding a second schema layer via protobuf is redundant.

**Operational simplicity.** WebSocket works through the same reverse proxies and load balancers that handle the client-facing WebSocket connections. gRPC requires HTTP/2 end-to-end, which adds configuration requirements, especially in environments with older infrastructure.

---

## Consequences

- The connector library (`Keryhe.Switchboard.Connector`) will use `ClientWebSocket` to maintain connections to the service.
- Frame parsing must be implemented manually (using `System.IO.Pipelines` and the `\x1e` delimiter or a length prefix).
- If gRPC becomes desirable later (e.g., for strongly typed multi-language server SDKs), it can be added as a second transport without changing the core protocol semantics.

---

## Alternatives Considered

**gRPC bidirectional streaming**  
Would provide strong typing and cross-language codegen. Rejected because protocol complexity is low, gRPC adds operational overhead, and HTTP/2 requirement complicates certain network environments.

**TCP socket (raw)**  
Maximum control. Rejected because it bypasses all standard HTTP infrastructure (load balancers, reverse proxies, health checks) and complicates TLS configuration.

**HTTP/1.1 long polling (server-to-service)**  
Poor fit for high-frequency bidirectional messaging. Rejected immediately.
