# Inbound Dispatch — Corrections Found During the Spike

Two real defects in the design doc's own code sketches were found by writing tests that actually
exercised the described behavior, rather than by static review. Both are fixed in the spike code
and are proposed as corrections to [04-design.md §11](../../docs/docs/04-design.md#11-connector--inbound-dispatch-synthetic-client-connections).

## 1. The identity-reconstruction snippet silently authenticates anonymous connections

**The design doc's code:**
```csharp
var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Switchboard"));
```
unconditionally passes a non-null `authenticationType`, regardless of whether `envelope.UserId` was present.

**Why this is a bug:** `ClaimsIdentity.IsAuthenticated` is `true` whenever `AuthenticationType` is
non-null/non-empty — **regardless of how many claims the identity carries**. A connection with
zero claims (a genuinely anonymous client — no `userId`, no custom claims) would still produce
`IsAuthenticated == true`, so `[Authorize]` on a hub method would incorrectly **permit** anonymous
connections.

**Found by:** [`IdentityFlowTests.Without_identity_authorized_method_is_denied`](../Phase0.Spike.Tests/WorkstreamB/IdentityFlowTests.cs) — written to assert the design doc's own stated requirement ("a hub method is permitted with the principal and rejected without it"), it failed on first run: `SecretEcho` ([Authorize]) succeeded for a connection built with `userId: null, claims: null`.

**Fix applied** ([`IdentityReconstruction.cs`](../Phase0.Spike.Connector/Dispatch/IdentityReconstruction.cs)):
```csharp
var authenticationType = userId is not null ? "Switchboard" : null;
return new ClaimsPrincipal(new ClaimsIdentity(identityClaims, authenticationType));
```
Only mark the identity authenticated when there's an actual identity (`userId`) to assert. A
connection with only free-standing custom claims and no `userId` is a judgment call Phase 1
should make explicitly rather than inherit by accident — flagged, not resolved, here.

**Proposed doc correction:** update the code sketch in [04-design.md §11](../../docs/docs/04-design.md#11-connector--inbound-dispatch-synthetic-client-connections) to make `authenticationType` conditional on `userId`, and add a note explaining *why* (the `IsAuthenticated` semantics above) so a future re-implementation doesn't reintroduce this silently.

## 2. The rejection-path close frame has no `allowReconnect` field at all (.NET 10)

**The design doc says** ([04-design.md §11](../../docs/docs/04-design.md#11-connector--inbound-dispatch-synthetic-client-connections)): "a hub whose `OnConnectedAsync` throws produces a close frame with `allowReconnect: false`."

**What actually happens**, confirmed by capturing the raw frame bytes from `RejectingHub` in .NET 10:
```json
{"type":7,"error":"Connection closed with an error."}
```
There is no `allowReconnect` field in this message at all — not `true`, not `false`, simply absent.

**Practical impact:** none observed — SignalR client libraries treat a missing `allowReconnect`
as `false` by default, so end-to-end behavior matches what the design doc intended. But **the
wire shape itself differs** from the doc's code sketch, which is worth recording precisely since
Part 2 of [03-protocol.md](../../docs/docs/03-protocol.md) reuses `close_connection` for this path
and a future implementer might otherwise assume the field is always present.

**Found by:** [`LifecycleAndRejectionTests.Rejecting_hub_produces_a_close_frame_with_allowReconnect_false_and_completes`](../Phase0.Spike.Tests/WorkstreamB/LifecycleAndRejectionTests.cs) — the original assertion (`message.GetProperty("allowReconnect")`) threw `KeyNotFoundException` rather than failing a boolean comparison, immediately surfacing that the field wasn't merely `true` but missing outright.

**Fix applied:** the test now asserts `type == 7` and `error` is present, and only checks
`allowReconnect`'s value if the field exists at all.

**Proposed doc correction:** update the close-frame example in [04-design.md §11](../../docs/docs/04-design.md#11-connector--inbound-dispatch-synthetic-client-connections) to show the actual .NET 10 shape (no `allowReconnect` key), and note that client libraries default a missing field to `false`.

## Why these matter beyond the spike

Both were caught only because the tests asserted the *specific* behavior the design doc claimed,
rather than a looser "something reasonable happened." This is the exact value Phase 0 is meant to
provide — a design assumption written against framework behavior nobody had run yet, corrected
before Phase 1 builds on it.
