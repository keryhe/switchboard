# Phase 0 API Recon — A0 & B0

Verified by reflection against the installed .NET 10 SDK (`10.0.201`, shared framework `Microsoft.AspNetCore.App 10.0.5`) on 2026-07-26, before writing any spike code. All symbols below are **public** in the ref assemblies and match [04-design.md](../../docs/docs/04-design.md)'s assumptions — no surprises, no fallback needed for either workstream.

## A0 — Negotiate interception

| Symbol | Confirmed | Notes |
|---|---|---|
| `Microsoft.AspNetCore.Http.Connections.NegotiateMetadata` | ✅ public, parameterless public ctor | Namespace matches the design doc. This is the marker `MapHub<T>()` attaches to the negotiate endpoint. |
| `Microsoft.AspNetCore.Routing.MatcherPolicy` | ✅ public abstract class | **Namespace correction:** it is `Microsoft.AspNetCore.Routing.MatcherPolicy`, not `Microsoft.AspNetCore.Routing.Matching.MatcherPolicy` as the design doc's prose implied. `IEndpointSelectorPolicy` and `CandidateSet` *are* in `Microsoft.AspNetCore.Routing.Matching`. |
| `Microsoft.AspNetCore.Routing.Matching.IEndpointSelectorPolicy` | ✅ public interface | `bool AppliesToEndpoints(IReadOnlyList<Endpoint>)`, `Task ApplyAsync(HttpContext, CandidateSet)` — exact signatures assumed by the design doc. |
| `CandidateSet.ReplaceEndpoint(int index, Endpoint endpoint, RouteValueDictionary values)` | ✅ public instance method | Exact signature assumed by the design doc. |
| `CandidateSet.Count` / indexer / `IsValidCandidate` / `SetValidity` | ✅ public | Needed to enumerate and identify candidates before replacing. |

**Runtime confirmation that `MapHub` actually attaches `NegotiateMetadata` and copies hub attributes** is done empirically in A1/A3 (running host + endpoint dump), not by static reflection — attribute-copying is inline behavior inside `HubEndpointRouteBuilderExtensions`, not a separate discoverable type.

## B0 — Inbound dispatch

| Symbol | Confirmed | Notes |
|---|---|---|
| `Microsoft.AspNetCore.Connections.ConnectionBuilder` + `.Use(...)` / `.Build()` | ✅ public | |
| `ConnectionBuilderExtensions.UseConnectionHandler<TConnectionHandler>()` | ✅ public generic extension method | |
| `HubConnectionHandler<THub> : ConnectionHandler` | ✅ public, `ConnectionHandler` is public with `Task OnConnectedAsync(ConnectionContext)` | Confirms `UseConnectionHandler<HubConnectionHandler<THub>>()` type-checks. |
| `Microsoft.AspNetCore.Connections.ConnectionItems : IDictionary<object,object>` | ✅ public, ctors: parameterless and `(IDictionary<object,object>)` | Design doc's `new ConnectionItems()` sketch is valid as written — no substitution needed. |
| `IConnectionUserFeature`, `IConnectionIdFeature`, `IConnectionItemsFeature`, `IConnectionHeartbeatFeature` | ✅ all public interfaces in `Microsoft.AspNetCore.Connections.Features` | Exact shape assumed by the design doc. |
| **Additional features found, not in the design doc's four-item list:** `IConnectionLifetimeFeature` (`ConnectionClosed` token + `Abort()`), `IConnectionCompleteFeature` (`OnCompleted` callback registration), `IConnectionEndPointFeature`, `IConnectionTransportFeature` | ⚠️ | `IConnectionLifetimeFeature` is load-bearing in practice — `HubConnectionHandler` needs `ConnectionClosed` to detect abort and `Abort()` to tear down. Added to the synthetic context alongside the documented four. `IConnectionTransportFeature` is effectively redundant with `ConnectionContext.Transport` itself but some framework code paths read the feature rather than the property, so the synthetic context sets both. See B1 code and `required-connection-features.md`. |
| `HandshakeProtocol.WriteRequestMessage(HandshakeRequestMessage, IBufferWriter<byte>)` | ✅ public static | Exact signature assumed by the design doc. |
| `HandshakeRequestMessage(string protocol, int version)` ctor | ✅ public | |

## Conclusion

No fallback triggered for either workstream at the recon stage. Every symbol the design doc's code sketches reference exists, is public, and matches the assumed signature — with one addition: `IConnectionLifetimeFeature` and `IConnectionCompleteFeature` are required in practice beyond the documented four, discovered here and confirmed against actual dispatcher behavior in B5 (lifecycle/rejection tests). This is fed back into [04-design.md §11](../../docs/docs/04-design.md#11-connector--inbound-dispatch-synthetic-client-connections)'s feature table as a doc correction (see [00-review-findings.md](../../docs/docs/00-review-findings.md) update).
