# Switchboard

A self-hosted connection proxy and scale-out backplane for ASP.NET Core SignalR, written in .NET 10 C#. Lets app servers offload and fan out real-time WebSocket connections.

## Documentation

| Document | Description |
|---|---|
| [00 — Open Review Findings](docs/00-review-findings.md) | Tracked open questions from documentation review — resolve before/during Phase 1 |
| [01 — Overview](docs/01-overview.md) | Goals, problem statement, comparison to alternatives, glossary |
| [02 — Architecture](docs/02-architecture.md) | System topology, component breakdown, data flow diagrams |
| [03 — Protocol](docs/03-protocol.md) | Client protocol, server protocol, management REST API specification |
| [04 — Design](docs/04-design.md) | Detailed component design, interfaces, algorithms |
| [05 — Data Models](docs/05-data-models.md) | Core data structures and configuration models |
| [06 — Project Plan](docs/06-project-plan.md) | Solution structure, NuGet dependencies, phased implementation roadmap (Phase 0 spike + Phases 1–5) |
| [08 — Sample App](docs/08-sample-app.md) | Angular + ASP.NET Core API sample chat app — connection flow, ChatHub, ChatService, local dev setup |

### Architecture Decision Records

| ADR | Decision |
|---|---|
| [ADR-001](docs/07-adr/ADR-001-transport-protocol.md) | WebSocket for app server connections (vs. gRPC) |
| [ADR-002](docs/07-adr/ADR-002-connection-registry.md) | In-memory registry (Phase 1), Orleans grains (Phase 3) |
| [ADR-003](docs/07-adr/ADR-003-backplane.md) | Orleans Grain Observers backplane (vs. Redis Pub/Sub, vs. Orleans Streams) |
| [ADR-004](docs/07-adr/ADR-004-token-authority.md) | Self-issued JWT (vs. external identity provider) |
| [ADR-005](docs/07-adr/ADR-005-protocol-compatibility.md) | Maintain wire compatibility with SignalR client libraries |

## Quick Architecture Summary

```
Clients  ──negotiate──▶  Switchboard  ◀──WebSocket──  App Servers
         ◀──redirect──                             ──notify──▶
         ──WebSocket──▶  (proxy + backplane)
```

1. Client negotiates with the app server (or directly with this service)
2. Client receives a redirect URL + short-lived JWT
3. Client opens a WebSocket directly to this service
4. This service forwards messages to/from the app server over a persistent connection pool
5. Broadcasts and group messages fan out to all relevant clients regardless of which app server originated them

## Technology

- .NET 10 / ASP.NET Core (Kestrel)
- `System.IO.Pipelines` for zero-copy message framing
- `System.Threading.Channels` for backpressure-aware write queues
- `System.IdentityModel.Tokens.Jwt` for token issuance and validation
- `Microsoft.Orleans.Server` for distributed registry and grain-observer backplane (Phase 3)
- `Microsoft.Orleans.Persistence.AdoNet` / `Microsoft.Orleans.Clustering.AdoNet` for SQL grain state (SQL Server or PostgreSQL)
- `MessagePack-CSharp` for binary hub protocol support
