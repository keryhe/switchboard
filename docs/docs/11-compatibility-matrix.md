# Compatibility Matrix

**Generated, not hand-maintained** — plan decision D36 ([plans/phase-5-compatibility-testing-and-benchmarking.md](../../plans/phase-5-compatibility-testing-and-benchmarking.md)). Produced by `tests/Keryhe.Switchboard.CompatibilityTests`'s matrix generation test, from a real run against a real out-of-process `Keryhe.Switchboard.Server` + `SampleChatApp.Api` pair. Do not hand-edit this file — its content is overwritten the next time that test runs.

Last generated: 2026-08-02 04:50:06 UTC

Every cell is one of exactly three states:

- **pass** — a real client of that SDK completed the full probe scenario (connect, receive a caller push, join a group, invoke a hub method, receive the resulting group message, disconnect cleanly) over that transport/protocol combination.
- **not applicable** — the SDK or the service correctly refuses this combination by design (e.g. SSE is Text-only; a given SDK version ships no MessagePack hub protocol or no SSE transport at all).
- **untested** — a required toolchain (e.g. Docker, for the Java row) was unavailable in the environment this document was generated in. Never silently omitted — see plan decision D34.

A failing cell fails the test run that generates this document; it never appears here as anything but one of the three states above.

## Matrix

| SDK | Transport | Protocol | Result | Note |
|---|---|---|---|---|
| .NET 8 (Microsoft.AspNetCore.SignalR.Client 8.0.29) | longpolling | json | pass |  |
| .NET 8 (Microsoft.AspNetCore.SignalR.Client 8.0.29) | longpolling | messagepack | pass |  |
| .NET 8 (Microsoft.AspNetCore.SignalR.Client 8.0.29) | sse | json | pass |  |
| .NET 8 (Microsoft.AspNetCore.SignalR.Client 8.0.29) | sse | messagepack | not applicable | SSE is Text-only by design |
| .NET 8 (Microsoft.AspNetCore.SignalR.Client 8.0.29) | websockets | json | pass |  |
| .NET 8 (Microsoft.AspNetCore.SignalR.Client 8.0.29) | websockets | messagepack | pass |  |
| Java (com.microsoft.signalr 9.0.6) | longpolling | json | not applicable | Known SDK limitation, verified: this client version's LongPollingTransport does not authenticate its establishing request |
| Java (com.microsoft.signalr 9.0.6) | longpolling | messagepack | not applicable | SDK ships no MessagePack hub protocol |
| Java (com.microsoft.signalr 9.0.6) | sse | json | not applicable | SDK's TransportEnum has no SERVER_SENT_EVENTS member |
| Java (com.microsoft.signalr 9.0.6) | websockets | json | pass |  |
| Java (com.microsoft.signalr 9.0.6) | websockets | messagepack | not applicable | SDK ships no MessagePack hub protocol |
| JavaScript (@microsoft/signalr 10.0.0) | longpolling | json | pass |  |
| JavaScript (@microsoft/signalr 10.0.0) | longpolling | messagepack | pass |  |
| JavaScript (@microsoft/signalr 10.0.0) | sse | json | pass |  |
| JavaScript (@microsoft/signalr 10.0.0) | sse | messagepack | not applicable | SSE is Text-only by design |
| JavaScript (@microsoft/signalr 10.0.0) | websockets | json | pass |  |
| JavaScript (@microsoft/signalr 10.0.0) | websockets | messagepack | pass |  |
| JavaScript (@microsoft/signalr 8.0.17) | longpolling | json | pass |  |
| JavaScript (@microsoft/signalr 8.0.17) | longpolling | messagepack | pass |  |
| JavaScript (@microsoft/signalr 8.0.17) | sse | json | pass |  |
| JavaScript (@microsoft/signalr 8.0.17) | sse | messagepack | not applicable | SSE is Text-only by design |
| JavaScript (@microsoft/signalr 8.0.17) | websockets | json | pass |  |
| JavaScript (@microsoft/signalr 8.0.17) | websockets | messagepack | pass |  |

## Known Incompatibilities

Documented behavioral differences from an unmodified SignalR deployment, each pinned by an executable assertion (plan decision D31) so a silent behavior change fails a test rather than only going stale in prose.

- **Context.GetHttpContext() is always null.** There is no IHttpContextFeature on the Connector's synthetic connection — no HTTP request exists on the app server for a proxied client. Hub code that reads headers, cookies, or RemoteIpAddress from GetHttpContext() will NPE. Pinned by KnownIncompatibilityTests.GetHttpContext_IsAlwaysNull_OnASyntheticConnection. Pass claims that data as claims through the negotiate forwarding instead.
- **A custom IUserIdProvider silently diverges from the service's user index.** The service's send_to_user routing uses the userId captured at negotiate; Context.UserIdentifier is computed independently by IUserIdProvider on the app server. A custom provider that derives the id differently makes Clients.User(Context.UserIdentifier) target a user the service's index doesn't recognize — sends land nowhere, with no exception. Pinned by KnownIncompatibilityTests.CustomUserIdProvider_DivergesFromServicesUserIndex_SilentlyDropsSends. Apps with a custom provider must apply the same logic when supplying userId on the forwarded negotiate.
- **Stateful reconnect falls back to standard reconnect.** A client requesting .WithStatefulReconnect() still connects and works normally — it just doesn't get buffered-message replay on resume, which this project treats as a non-goal (message persistence/replay). Pinned by KnownIncompatibilityTests.StatefulReconnectRequest_FallsBackGracefully_ConnectionStillWorks.
- **Client results (Clients.Client(id).InvokeAsync<T>(...)) are not supported.** Hub code that calls it gets a Switchboard-specific NotSupportedException naming the limitation, not the framework's bare NotImplementedException. Correctly routing the completion back to the originating app server needs a new correlated-completion path across the cluster-wide server-connection assignment (plan decision D18) — out of scope for this phase. Pinned by ClientResultsTests.HubCallingClientResults_SurfacesASwitchboardSpecificError_NotABareNotImplementedException. See 04-design.md §14.
- **SSE is Text-only; SSE+MessagePack is refused, not silently broken.** Negotiate advertises ServerSentEvents with transferFormats: ["Text"] only, so a client cannot request MessagePack over SSE at all (03-protocol.md §1.5).
