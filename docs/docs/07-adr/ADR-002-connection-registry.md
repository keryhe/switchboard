# ADR-002: In-Memory Registry (Phase 1), Orleans Grains (Phase 3)

**Status:** Accepted  
**Date:** 2025-10

---

## Context

The connection registry stores all active client connection state and group membership. It must support:
- Fast lookup by `connectionId` on every message route
- Enumeration of all connections for a hub (broadcast)
- Group membership queries
- Updates on connect/disconnect/group-change

The registry design determines whether the service can run clustered.

---

## Decision

**Phase 1:** Pure in-memory registry using `ConcurrentDictionary`. No external dependencies.

**Phase 3:** Orleans grain-based distributed registry. In-memory remains the default for single-node deployments.

---

## Rationale

### In-Memory (Phase 1)

**Latency.** Every message route involves a registry lookup. In-memory lookups are nanoseconds. Any network-backed store adds latency per lookup. The MVP should have zero unnecessary overhead.

**Simplicity.** No external dependencies. The MVP can run as a single self-contained binary with no infrastructure.

**Sufficient for single-node.** If the deployment never needs to scale beyond one service node, in-memory is the right tool permanently.

**Tradeoff:** State is lost on process restart. Clients must reconnect. This matches SignalR's existing behavior — no message persistence or state durability guarantees.

### Orleans Grains (Phase 3)

**Shared state without a separate system.** Orleans grains provide distributed state as a native primitive. Since Orleans is already hosting the backplane (ADR-003), the connection registry can be implemented as grain state in the same silo — no additional infrastructure (no Redis, no SQL just for the registry).

**Virtual actor model maps naturally to the domain.** A `HubGrain` knows all connections for its hub. A `GroupGrain` owns its member set. A `ConnectionGrain` knows which node owns a given client. These are direct conceptual mappings, not an impedance-mismatch with a key-value store.

**Automatic placement and failover.** If a service node fails, grains that were activated on it reactivate on surviving nodes. The registry recovers without operator intervention.

**Consistent lookup across nodes.** A call to `IConnectionGrain.GetOwnerNodeAsync(connectionId)` returns the correct owning node from any node in the cluster. No cross-node broadcast scan is needed to find a connection.

**Tradeoff:** Grain calls for targeted lookups involve an RPC hop when the grain is activated on a different node (~1–5ms). For broadcast, the `HubGrain` holds the full connection set and is the fan-out coordinator — this is a single grain call rather than a registry scan.

---

## Grain Topology

```
IConnectionGrain   (key: connectionId)
  → ownerNodeId, hubName, userId, connectedAt

IHubGrain          (key: hubName)
  → set of { connectionId, nodeId }
  → handles broadcast fan-out across all nodes

IGroupGrain        (key: "hubName::groupName")
  → set of connectionIds
  → handles group fan-out

IUserGrain         (key: "hubName::userId")
  → set of connectionIds
  → handles user-targeted fan-out
```

Each service instance registers a local `HubObserverImpl` (an `IHubObserver` / `IGrainObserver`, not a grain) with the hub grains. It receives inbound delivery calls from hub/group/user grains and resolves them to local `IClientTransport` handles via the node-local `ILocalTransportRegistry`. See [ADR-003](ADR-003-backplane.md) for the observer backplane design.

---

## Consequences

- The `IConnectionRegistry` interface must be async from Phase 1, even though in-memory operations are synchronous. This ensures Phase 3 substitution requires no interface changes.
- The in-memory implementation must **not** store `IClientTransport` references in any way that could be confused with distributed state — transport handles are always local-node concerns.
- Orleans grain interfaces (`IHubGrain`, `IGroupGrain`, `IUserGrain`, `IConnectionGrain`) and the `IHubObserver` observer interface are defined in `Keryhe.Switchboard.Orleans` and are called by `OrleansConnectionRegistry` and `OrleansObserverBackplane`.
- Grain state persistence requires a storage provider. `Microsoft.Orleans.Persistence.AdoNet` (SQL Server or PostgreSQL) is recommended for production. `Microsoft.Orleans.Persistence.Memory` is used in development and single-node mode.

---

## Alternatives Considered

**Redis hash-based registry**  
Was the original Phase 3 plan. Rejected once Orleans was chosen — adding Redis solely for the registry when Orleans grains provide the same capability natively adds an unnecessary external dependency.

**SQLite or relational database (direct)**  
Offers durability. Rejected: connection metadata has no value after a process restart; query patterns (lookup by ID, enumerate by hub) don't benefit from SQL schemas. Orleans ADO.NET provider gives SQL durability for grain state without writing queries directly.

**`IDistributedCache` (ASP.NET Core abstraction)**  
Too generic — does not model group membership or user→connection indexes. Would require custom serialization for all state and a custom query layer.
