# Switchboard — Project Overview

## What Is This?

This project implements an on-premise equivalent of Azure SignalR Service: a standalone infrastructure service written in .NET 10 C# that acts as both a **connection proxy** and a **scale-out backplane** for ASP.NET Core SignalR applications.

Application servers do not hold persistent client connections. Instead, clients connect to this service, and the service forwards messages between clients and app servers over a smaller set of long-lived server connections. When multiple app servers are deployed, the service automatically routes messages across them — eliminating the need for sticky sessions and solving the classic SignalR scale-out problem without any cloud dependency.

---

## Problem Statement

ASP.NET Core SignalR keeps WebSocket (or fallback transport) connections open on the app server process. This creates two compounding problems at scale:

1. **Connection overhead on app servers.** Each connected client consumes a file descriptor, thread-pool work, and memory on the server. A 10,000-client deployment requires app servers sized for 10,000 concurrent connections, not for application logic.

2. **Broken broadcasts in scale-out.** When app servers are load-balanced, a broadcast from Server A only reaches clients connected to Server A. Clients on Server B are silently missed. Sticky sessions work around this but tie clients to specific instances, making rolling deploys painful.

**Redis backplane** solves problem 2 but not problem 1 — app servers still hold all connections and sticky sessions are still typically required.

**Azure SignalR Service** solves both, but requires connectivity to Azure and a paid cloud service.

This project solves both problems with an on-premise service you own and operate.

---

## How It Solves the Problems

### Connection Offloading (Problem 1)

Clients connect to the SignalR Service, not to app servers. App servers maintain a small, fixed pool of WebSocket connections to the service (default: 5 per hub). 100,000 clients → 5 connections on each app server. App server sizing is driven by message processing load, not connection count.

### Backplane (Problem 2)

Because all clients connect to the service, the service has a complete view of every connected client regardless of which app server they are paired with. Broadcasts and group messages are fanned out from the service to all relevant clients. No sticky sessions required.

---

## Comparison to Alternatives

| Feature | This Service | Redis Backplane | Azure SignalR Service |
|---|---|---|---|
| Eliminates sticky sessions | Yes | Partial | Yes |
| Offloads connections from app servers | Yes | No | Yes |
| On-premise deployable | Yes | Yes | No |
| No external cloud dependency | Yes | Yes | No |
| No Redis required | Yes | No | No |
| Supports WebSocket + SSE + Long Polling | Yes | N/A | Yes |
| Clusterable (multiple service nodes) | Yes (Phase 3) | N/A | Yes (managed) |
| Compatible with existing SignalR clients | Yes | Yes | Yes |
| Open source / self-hosted | Yes | Yes | No |

---

## Target Deployment Scenarios

### Single-Node (MVP)
A single instance of the service running on one machine. Suitable for development, small deployments, or environments where HA is handled at a higher layer (e.g., VM failover). Connection state is held entirely in memory.

### Clustered (Phase 3)
Multiple service nodes behind a load balancer. Clients can connect to any node. Each service node runs an Orleans silo; connection state is held in Orleans grains, and messages between nodes flow via Orleans grain observers (each node registers a local observer with the hub grain). A shared SQL database (SQL Server, PostgreSQL, or MySQL) stores grain state and the Orleans cluster membership table. This mirrors the Azure SignalR Service topology without any cloud dependency.

```
                    ┌─────────────────┐
                    │  Load Balancer  │
                    └────────┬────────┘
               ┌─────────────┴─────────────┐
               ▼                           ▼
     ┌──────────────────┐       ┌──────────────────┐
     │  Service Node A  │       │  Service Node B  │
     │  (Orleans Silo)  │◄─────►│  (Orleans Silo)  │
     └──────────────────┘       └──────────────────┘
          Orleans cluster membership + grain directory
               │                           │
               └──────────┬────────────────┘
                           ▼
                  ┌───────────────────────┐
                  │ SQL Server / Postgres /│
                  │ MySQL (grain state +  │
                  │ cluster table)        │
                  └───────────────────────┘
```

---

## Non-Goals

The following are explicitly out of scope for this project:

- **Azure Functions / Serverless mode.** This service targets the "Default" mode where ASP.NET Core hub servers are present. There is no plans for a Functions-style trigger binding.
- **Multi-tenancy.** A single deployment serves a single application. Tenant isolation is not a design goal.
- **Message persistence / replay.** If a client is disconnected, messages sent during that time are not queued or replayed. Behavior matches standard SignalR (fire-and-forget). This also means **stateful reconnect** (`.withStatefulReconnect()`, .NET 8+) is not supported — buffering un-acked messages per connection for replay on resume is costly in a proxy topology (100k+ per-connection buffers) and complex under clustering (the buffer lives on the owning node; a reconnect landing elsewhere, or after that node fails, cannot resume it). Clients that request it fall back to standard reconnect. See [ADR-005](07-adr/ADR-005-protocol-compatibility.md#what-is-not-in-scope-for-compatibility). Deferred as a candidate future enhancement.
- **Client results** (`Clients.Client(id).InvokeAsync<T>(...)`, .NET 8+). A structurally identical problem to stateful reconnect: correctly routing the client's eventual completion back to the app server that made the call requires per-invocation state that doesn't survive this project's proxy/scale-out topology unmodified, since the invoking app server and the client's assigned server connection can be different processes (plan decision D18). Hub code that calls it gets a Switchboard-specific `NotSupportedException` naming the limitation rather than the framework's bare, unhelpful default. See [ADR-005](07-adr/ADR-005-protocol-compatibility.md#what-is-not-in-scope-for-compatibility) and [04-design.md §14](04-design.md#14-hublifetimemanager-coverage-phase-5). Deferred as a candidate future enhancement (Phase 5 plan decision D32).
- **End-to-end TLS termination at scale.** TLS is supported via Kestrel, but certificate management, rotation, and mTLS between nodes are out of scope. Use a reverse proxy (nginx, Caddy) in front of this service for production TLS.
- **Protocol translation.** This service speaks the SignalR protocol. It does not bridge to MQTT, AMQP, gRPC streams, or other protocols.
- **Full Azure SignalR Service API parity.** The management REST API covers the most common operations. Exotic features (tracing headers, shadow copy negotiation, custom routing policies) are not targeted.

---

## Glossary

| Term | Definition |
|---|---|
| **Client** | An end-user application (browser, mobile, desktop) using a SignalR client library to connect |
| **App Server** | The developer's ASP.NET Core application containing SignalR Hub classes |
| **Service** | This project — the Switchboard that proxies between clients and app servers |
| **Hub** | A SignalR concept representing a named channel with typed methods (e.g., `ChatHub`) |
| **Connection ID** | A unique GUID assigned to each client connection at negotiate time |
| **Server Connection** | A persistent WebSocket connection from an app server to this service |
| **Client Connection** | A persistent transport connection (WS/SSE/LP) from a client to this service |
| **Negotiate** | The HTTP handshake where a client receives the service URL and a short-lived access token |
| **Backplane** | The mechanism that routes messages between service nodes in a clustered deployment |
| **Transport** | The underlying connection mechanism: WebSocket, Server-Sent Events, or Long Polling |
| **Hub Protocol** | The message serialization format: JSON or MessagePack |
| **Invocation** | A client-to-server or server-to-client method call in the SignalR protocol |
| **Group** | A named set of connections that can be targeted as a unit for messaging |
