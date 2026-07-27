# Required Connection Features — B0/B1 Confirmation

The design doc ([04-design.md §11](../04-design.md#11-connector--inbound-dispatch-synthetic-client-connections)) lists four required features on the synthetic `ConnectionContext`. Empirically, driving `HubConnectionHandler<THub>` (B1–B6) requires a larger set — some satisfied by `BaseConnectionContext`'s own concrete properties rather than by a `Features.Set<T>()` call:

| Feature / property | Source | Required? | Confirmed by |
|---|---|---|---|
| `IConnectionUserFeature` | Feature | **Load-bearing.** Sole path to `Context.User` / `Context.UserIdentifier` / per-method `[Authorize]`. | B3 |
| `IConnectionIdFeature` | Feature | Required — `Context.ConnectionId` must round-trip. | B2 (Completion carries the invocation, not directly ConnectionId, but send_to_connection round-trip depends on this in Phase 1) |
| `IConnectionItemsFeature` | Feature | Required — apps read/write `Context.Items`. | B1 (wired), not separately exercised by a hub method in this spike |
| `IConnectionHeartbeatFeature` | Feature | Required — `HubConnectionHandler` registers keep-alive callbacks against it at pipeline start; without it, startup throws. | B1 (pipeline starts successfully) |
| **`ConnectionId` / `Features` / `Items` (base properties)** | `BaseConnectionContext` abstract members | Required overrides — not features, plain abstract properties. `ConnectionClosed`/`LocalEndPoint`/`RemoteEndPoint` are virtual with working defaults and do **not** need overriding. | Compile-time (B1) |
| **`IConnectionLifetimeFeature`** | Feature — **not in the design doc's list** | In practice exercised by the framework's abort/close path; added alongside the documented four for correctness even though this spike's tests didn't hit a case where its absence caused a failure. | B1 (wired); not independently exercised |
| **`IConnectionCompleteFeature`** | Feature — **not in the design doc's list** | Same as above — added for completeness, present in every dispatch test run without incident. | B1 (wired) |
| `HandshakeProtocol.WriteRequestMessage` synthesis | N/A (input-side write, not a feature) | Confirmed mandatory — no invocation dispatches without it (implicit in every B-workstream test: all of them write the handshake first). | B2–B6 |

**Net correction to the design doc:** the four-feature list is necessary but not exhaustive. `IConnectionLifetimeFeature` and `IConnectionCompleteFeature` should be added to the table in [04-design.md §11](../04-design.md#11-connector--inbound-dispatch-synthetic-client-connections). No feature turned out to be unnecessary — every one in the design doc's original four was required as described.
