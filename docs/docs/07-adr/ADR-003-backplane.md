# ADR-003: Orleans Grain Observers Backplane (vs. Redis Pub/Sub, vs. Orleans Streams)

**Status:** Accepted  
**Date:** 2025-10  
**Supersedes:** Initial draft that proposed Redis Pub/Sub; revised draft that proposed Orleans Streams

---

## Context

In a clustered deployment with multiple service nodes, a message originating at one node must reach clients connected to other nodes. This requires a backplane mechanism.

Orleans is already running for the distributed registry (ADR-002). The backplane mechanism must work within the same Orleans cluster without requiring additional infrastructure. Two Orleans-native options were evaluated:

1. **Orleans Streams** — backplane messages published to streams; each node subscribes via a stream provider
2. **Orleans Grain Observers** — each node registers a local observer object with the hub grain; the grain notifies all observers directly when broadcasting

---

## Decision

Use **Orleans Grain Observers** as the backplane mechanism.

---

## Rationale

**No stream provider infrastructure required.** Orleans Streams require a concrete stream provider. The built-in `MemoryStreamProvider` works within a single silo but not across silos in production clusters. All cross-silo stream providers (Azure Queue, EventHub, RabbitMQ) introduce external dependencies, which contradicts the goal of a self-contained on-premise service. Grain observers are a built-in Orleans primitive included in `Microsoft.Orleans.Server` — no additional packages or infrastructure.

**Direct push semantics.** When `IHubGrain.BroadcastAsync()` is called, it immediately invokes each registered `IHubObserver`. The observer call is an Orleans grain-to-object RPC — it executes in the silo that registered the observer, with direct access to `ILocalTransportRegistry`. There is no intermediate queue, no polling, no subscription management per stream key.

**Self-echo solved cleanly.** `BroadcastAsync` accepts `originNodeId`. The hub grain maintains `Dictionary<string, IHubObserver>` keyed by `nodeId`. It skips notifying the observer registered by `originNodeId` — that node already fanned out to its local clients before calling the grain. No additional filtering needed at the observer layer.

**Simpler lifecycle.** With streams, each node must subscribe and unsubscribe as hubs come and go, and subscription handles must be persisted to survive silo restarts. With observers, a node calls `IHubGrain.SubscribeAsync(observer, nodeId)` at startup and `UnsubscribeAsync(nodeId)` at shutdown. No subscription handle persistence required.

**Tradeoffs acknowledged:**
- Observer calls are fire-and-forget from the grain's perspective. If the receiving silo is temporarily unavailable, the observer call is lost. This matches standard SignalR behaviour — cross-node messages may be dropped under partition; local fan-out is unaffected.
- Grain observers are not guaranteed delivery. For real-time messaging where missed messages are acceptable (matching standard SignalR semantics), this is acceptable.

---

## Observer Design

```
IHubObserver (IGrainObserver)
  implemented by: HubObserverImpl (plain class, one per silo)
  has access to: ILocalTransportRegistry (local singleton)

IHubGrain (key: hubName)
  state: Dictionary<string, IHubObserver> _observers   // nodeId → observer ref
  state: Dictionary<string, string> _connectionToNode  // connectionId → nodeId

  SubscribeAsync(observer, nodeId)      → adds to _observers
  UnsubscribeAsync(nodeId)              → removes from _observers
  BroadcastAsync(payload, originNodeId) → calls all observers except originNodeId
```

Message flow:
```
AppServer → ServiceNodeA → local fan-out (ILocalTransportRegistry)
                         → IHubGrain.BroadcastAsync(originNodeId: "A")
                               HubGrain skips observer "A"
                               calls observer "B" → NodeB local fan-out
                               calls observer "C" → NodeC local fan-out
```

---

## Consequences

- `Microsoft.Orleans.Streaming` is **not** required. Removed from the NuGet package list.
- No stream provider configuration needed at any deployment phase.
- The `IBackplane` interface is implemented by `OrleansObserverBackplane`. The `NoOpBackplane` is used in single-node mode.
- Observer registration happens in `IHostedService.StartAsync` of the Orleans integration layer, once per hub that has an active server connection.
- If the Orleans cluster is partitioned, nodes in different partitions cannot exchange observer calls. Local fan-out still works. Behaviour matches standard SignalR failure semantics.

---

## Alternatives Rejected

**Orleans Streams**  
Initially selected. Rejected because no suitable cross-silo stream provider exists for on-premise deployments without introducing an external dependency (RabbitMQ, etc.). Grain observers provide equivalent push semantics with no infrastructure requirements.

**Redis Pub/Sub**  
Requires Redis as an external dependency. Redundant given Orleans is already present for the registry. Rejected.

**Node-to-node WebSocket mesh**  
O(N²) connections, requires node discovery, complex reconnect logic. Rejected regardless of registry choice.
