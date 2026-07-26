# ADR-005: Maintain Wire Compatibility with SignalR Client Libraries

**Status:** Accepted  
**Date:** 2025-10

---

## Context

This service could either:
1. **Implement the standard SignalR client protocol exactly** — existing client libraries (.NET, JavaScript, Java) connect without modification
2. **Define a custom protocol** — require custom client libraries or configuration

---

## Decision

**Maintain full wire compatibility** with the standard ASP.NET Core SignalR client protocol. Clients connect using unmodified, officially published SignalR client libraries.

---

## Rationale

**Zero client migration cost.** Applications already using SignalR can adopt this service by changing only the server-side configuration (swapping the connector package). No client code changes, no client library updates, no redeployment of browser-side JavaScript.

**Ecosystem leverage.** Microsoft maintains SignalR client libraries for .NET, JavaScript, Java, and Python. These libraries handle reconnect logic, transport fallback, protocol negotiation, and keep-alive. Implementing compatible service-side behavior means this project benefits from all that client-side work for free.

**Testability.** Compatibility can be verified by running standard client libraries against this service. Any deviation is a detectable test failure, not a subtle behavioral difference.

**Future-proofing.** New SignalR client protocol additions (e.g., new hub message types or transport refinements) can become available as this service implements the corresponding server-side behavior. Opt-in, negotiated features the service chooses not to implement (see stateful reconnect below) degrade gracefully rather than breaking clients.

---

## Scope of Compatibility

| Protocol Element | Compatibility Target |
|---|---|
| Negotiate endpoint format | Identical to ASP.NET Core SignalR negotiate response |
| WebSocket client transport | Full implementation |
| SSE client transport | Full implementation |
| Long Polling transport | Full implementation |
| JSON hub protocol | Full implementation (record separator `\x1e`) |
| MessagePack hub protocol | Full implementation (length-prefix framing) |
| Hub message types 1–7 | All implemented |
| Stateful reconnect | **Not supported** — negotiated opt-in feature; clients fall back to standard reconnect (see below) |
| Hub protocol version negotiation | v1 (v2 when needed) |

---

## What Is Not In Scope for Compatibility

- **Azure SignalR Service management API.** The management REST API in this project is similar in spirit but not identical in URL structure or authentication to the Azure SignalR Service data-plane REST API.
- **Azure SignalR Service SDK server-side (`AddAzureSignalR()`).**  The connector library (`AddSwitchboardConnector()`) is not a drop-in replacement at the NuGet package level — it requires a package swap. However, the hub code and client code are unaffected.
- **Undocumented Azure internals.** Features like shadow copy negotiation, Azure-specific tracing headers, or internal load-balancing signals are not replicated.
- **Stateful reconnect** (`.withStatefulReconnect()` / `WithStatefulReconnect()`, .NET 8+). This buffers un-acknowledged messages and replays them on resume — a form of message replay this project treats as a [non-goal](../01-overview.md#non-goals). Because the feature is negotiated and opt-in, a client that requests it against this service simply **falls back to standard reconnect** (which fires `OnDisconnected`/`OnReconnected` and does not preserve in-flight messages); the connection is not broken. Deferred as a candidate future enhancement — see the note in [01-overview.md Non-Goals](../01-overview.md#non-goals) on why per-connection buffering is costly in a proxy/clustered topology.

---

## Consequences

- Every Protocol Specification decision (see [03-protocol.md](../03-protocol.md)) must reference the upstream ASP.NET Core SignalR source code or documentation to confirm conformance.
- The integration test suite must include real Microsoft SignalR client library instances (not mocks) as the ground-truth compatibility check.
- When Microsoft ships a new SignalR client version with protocol changes, this service must be evaluated for compatibility and updated if necessary.
- Deviations from the standard protocol (e.g., additional envelope fields on the service-to-server protocol) must be strictly confined to the server-facing protocol and must never appear in the client-facing protocol.
