# Phase 2 — Full Transport & Protocol Support: Implementation Plan

**Source of truth:** [06-project-plan.md § Phase 2](../docs/docs/06-project-plan.md), [03-protocol.md Parts 1–2](../docs/docs/03-protocol.md), [04-design.md §§1, 5, 6, 9, 11](../docs/docs/04-design.md), [05-data-models.md](../docs/docs/05-data-models.md), [08-sample-app.md](../docs/docs/08-sample-app.md), [00-review-findings.md](../docs/docs/00-review-findings.md).

**Goal.** Full SignalR transport and protocol compatibility. An existing SignalR app needs zero code changes beyond adding the Connector package — whichever transport and hub protocol its clients pick.

**Milestone check.** `SampleChatApp.Angular` running in a browser negotiates through `SampleChatApp.Api`, opens a WebSocket to the proxy, and the full chat room flow (join, send, receive, leave) works. The standard .NET client is also tested over all three transports.

Phase 1 built one path end to end: WebSocket + JSON + targeted/broadcast routing. Phase 2 widens it in three independent directions — **transports** (SSE, Long Polling), **protocols** (MessagePack), and **routing targets** (groups, users) — and each direction has a seam that only breaks when it meets one of the others. The slice order below is chosen so those intersections are reached deliberately rather than discovered at the end.

---

## 1. Preconditions — what Phase 1 already settled

Phase 1 is complete and its milestone is green ([results](../docs/docs/00-review-findings.md)). Do **not** re-derive:

| Established | Consequence for Phase 2 |
|---|---|
| Two-step negotiate, `connectionToken`/`connectionId` split, D1 token-type dispatch | Unchanged. Pattern A (Slice 7) adds a *third* branch to the same dispatch, it does not alter the two existing ones |
| `payload` is raw hub-protocol bytes **including framing** | The single most expensive lesson of Phase 1. Slice 0 makes it structurally impossible to get wrong again — see **D8** |
| Synthetic-connection inbound dispatch works; identity flows via `IConnectionUserFeature` | Streaming and `CancelInvocation` ride the *same* path with no new mechanism — see **D12** |
| `IConnectionRegistry` already tracks group membership and a user index | Group/user *routing* (Slice 1) is a service-side change only; `add_to_group` already works |
| Connector emits correct envelopes for every one of the 13 `HubLifetimeManager` methods (D3) | Slice 1 is genuinely service-side-only, as D3 intended |
| `ServerEnvelope` `[Key(0..10)]` is a wire contract; append only | **D7** appends `[Key(11)]` and must not touch anything below it |
| Hub route names must be a single path segment | Applies to any new route Phase 2 maps (`POST /{hub}`, `DELETE /{hub}`) |

### New framework facts verified while writing this plan

Checked empirically against the real framework (`Microsoft.AspNetCore.SignalR.Protocols.MessagePack` 10.0.10, `Microsoft.AspNetCore.SignalR.Client` 10.0.10, `MessagePack` 3.1.8) rather than assumed. Each of these invalidates something a reasonable implementer would otherwise guess:

1. **The hub-protocol handshake is always JSON delimited by `\x1e`, even when the negotiated protocol is MessagePack.** A real MessagePack client sends exactly `{"protocol":"messagepack","version":1}\x1e`, and the response is `{}\x1e`. Only the messages *after* the handshake switch framing. So the handshake parse/write path does **not** become protocol-dependent — but everything after it does, and the switchover point is precisely one frame.

2. **…but the MessagePack client sends that JSON handshake inside a WebSocket *Binary* frame**, while the JSON client sends *Text*. Verified by capturing the first frame from a real `HubConnection` on a raw WebSocket endpoint. `WebSocketClientTransport` currently hardcodes `WebSocketMessageType.Text` on every write ([WebSocketClientTransport.cs:130](../src/Keryhe.Switchboard.Server/ClientConnections/WebSocketClientTransport.cs:130)), so the write side must follow the negotiated protocol's `TransferFormat` — **including for the handshake response**, whose payload is JSON but whose frame type must be Binary for a MessagePack client.

3. **The handshake `version` is `1` for both protocols** — it is the *handshake* version, not `IHubProtocol.Version`. Worth stating because `MessagePackHubProtocol.Version` reports **2**, and validating the handshake against that number would reject every real MessagePack client. `03-protocol.md §1.2`'s "messagepack (version 1)" is correct as written.

4. **MessagePack framing is a 7-bit varint length prefix, LSB-first, high bit = continuation.** Verified: an 18-byte frame carries prefix `0x11` (17 payload bytes); a 215-byte frame carries `0xD5 0x01` (0x55 | (1 << 7) → 213). Splitting frames therefore needs ~30 lines and **no** MessagePack deserialization — the service never has to understand a client's hub messages, only find their boundaries.

5. **`Microsoft.AspNetCore.SignalR.Protocols.MessagePack` coexists with the repo's `MessagePack` 3.1.8.** The package is built against MessagePack 2.x; NuGet unifies to 3.1.8 and `MessagePackHubProtocol` still writes correct frames — verified by round-tripping an `InvocationMessage` and a `CloseMessage` with 3.1.8 forced. This removes the version-conflict objection to taking the dependency (see **D9**).

6. **Use 10.0.10, not 10.0.0.** NuGet audit flags `Microsoft.AspNetCore.SignalR.Protocols.MessagePack` 10.0.0 with a high-severity advisory (GHSA-f8h2-vmm9-qhj6) and drags in `MessagePack` 2.5.187, which carries several more. At 10.0.10 with `MessagePack` 3.1.8 the restore is audit-clean. The repo currently pins `Microsoft.AspNetCore.SignalR.Client` at **10.0.0** in both test projects — bump those in the same change.

7. **Re-serializing a parsed hub message is lossy in the general case.** The Connector's outbound reader parses with an `EmptyBinder` whose `GetReturnType` is `typeof(object)` and then re-serializes ([InboundDispatcher.cs:148](../src/Keryhe.Switchboard.Connector/Dispatch/InboundDispatcher.cs:148)). A JSON `Completion` carrying an anonymous object does survive that round trip (verified), but it survives by accident — the result is reconstructed from a `JsonElement`. Under MessagePack it will not. See **D12**.

---

## 2. Decisions

Seven decisions, numbered **D7–D13** continuing Phase 1's D1–D6 so that a code comment saying "plan decision D3" stays unambiguous. Each is a recommendation, ready to implement as written; revisit only if implementation contradicts one.

### D7 — Mixed-protocol fan-out: how does one `Clients.All.SendAsync` reach both a JSON and a MessagePack client?

This is the hardest design question in Phase 2 and it is invisible until MessagePack and fan-out both exist.

`HubLifetimeManager.SendAllAsync(methodName, args)` is protocol-agnostic — the Connector must serialize before it can put bytes in an envelope. But `ServerEnvelope` carries exactly one `Payload` and one `HubProtocol`, and a hub's clients may be split across both protocols. A JSON `broadcast` payload written verbatim to a MessagePack client's socket is garbage.

Rejected alternatives:

- **Service transcodes JSON → MessagePack.** The service does not know the argument types, so it would have to round-trip through `object`. That is exactly the lossy path finding 7 warns about, and it would force the service to deserialize client payloads — abandoning the raw-bytes passthrough contract that the whole server-facing protocol is built on. No.
- **Connector serializes only for the protocols it has observed** on its own live connections. Unsafe: with multiple app-server instances, a broadcast originating on instance A must reach clients whose `open_connection` went to instance B, whose protocol set A has never seen.

**Recommendation — a payload set for fan-out, a single payload for targeted sends.** Append `[Key(11)] IReadOnlyDictionary<string, byte[]>? Payloads` to `ServerEnvelope`, keyed by protocol name. Then:

| Envelope | Payload carrier | Why |
|---|---|---|
| `client_message`, `send_to_connection` | existing `Payload` + `HubProtocol` | Exactly one target, whose protocol the sender knows: the service knows it for `client_message`, and the Connector knows it from `open_connection` for `send_to_connection` |
| `broadcast`, `send_to_group`, `send_to_user` | new `Payloads` | Target set is unknown to the sender and may be protocol-mixed |

The Connector populates `Payloads` with an entry for **every protocol the service supports** (`json` and `messagepack`), unconditionally. That is one extra serialization per fan-out call, which is the price of correctness across app-server instances; it is what the Azure SignalR SDK does, and if it shows up in the Phase 5 benchmarks it can be optimized then with real numbers.

**If a target connection's protocol has no entry in `Payloads`, the message is dropped and logged at warning** — never written half-encoded, never silently discarded. This is the same "assert absence, don't fail silently" posture as D3.

`Payload`/`HubProtocol` stay populated on fan-out envelopes with the JSON encoding for one release, so a Phase 1-era peer keeps working; drop that at the start of Phase 3. **`[Key(0..10)]` must not move.**

### D8 — Frame readers yield frames *with* their framing

Phase 1 lost a day to `JsonFrameProtocol.TryParseFrame` stripping the `\x1e`, `ClientConnectionEndpoint` handing the stripped frame to the router, and the Connector writing it into a pipe where nothing could ever parse it. The fix was to re-add the delimiter at the boundary ([`FrameForServer`](../src/Keryhe.Switchboard.Server/ClientConnections/ClientConnectionEndpoint.cs:154)). That fix is correct and load-bearing — and it is one call site away from regressing, in a phase that adds three more transports and a second framing format.

**Recommendation: invert the default.** Introduce a `IHubProtocolFraming` abstraction in `Keryhe.Switchboard.Protocol` with `Json` and `MessagePack` implementations:

```csharp
public interface IHubProtocolFraming
{
    string Name { get; }                        // "json" | "messagepack"
    TransferFormat TransferFormat { get; }      // Text | Binary  (drives the WebSocket message type — finding 2)
    bool TryReadFrame(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> frame);  // frame INCLUDES its framing
    void WriteFrame(IBufferWriter<byte> writer, ReadOnlySpan<byte> message);
}
```

Every reader yields frames that are already valid `payload` values, so `FrameForServer` is deleted rather than duplicated per transport. The service's only remaining reason to look *inside* a frame is D13's ping classification.

Keep `JsonFrameProtocol` as-is underneath if convenient — the point is that no caller ever again holds a delimiter-stripped frame it must remember to re-frame.

### D9 — Does the service take a hub-protocol dependency?

The service is deliberately payload-agnostic, but it must *write* three things itself: the handshake response, a hub-level `Close` (server-connection loss, app-server rejection), and hub-level `Ping` (D13). For a MessagePack client those must be MessagePack-encoded.

Finding 5 removes the version-conflict objection and finding 6 gives a clean version, so:

**Recommendation: take `Microsoft.AspNetCore.SignalR.Protocols.MessagePack` 10.0.10 in `Keryhe.Switchboard.Protocol`, and confine it to a single `ClientFrameWriter` type** whose entire API is "give me a framed handshake-response / Close / Ping for protocol X". Nothing else in the service may reference `IHubProtocol`. Hand-rolling those frames against `MessagePackWriter` was the alternative and it is not worth the byte-fiddling now that the dependency is proven safe.

Guard it with byte-pinned tests comparing `ClientFrameWriter`'s output against the framework's own `MessagePackHubProtocol`/`JsonHubProtocol` output. If someone later widens `ClientFrameWriter` into a general parser, that is a review failure, not a build failure — so say so in its XML docs.

### D10 — A client connection must outlive a single HTTP request

`ClientConnectionEndpoint.HandleAsync` currently *is* the connection: it validates, handshakes, registers, runs the read loop, and tears down, all inside one WebSocket request. SSE and Long Polling cannot work that way — an SSE connection spans one long-lived GET plus many short POSTs, and a Long Polling connection spans an unbounded series of short GETs.

**Recommendation: extract the lifecycle from the request.** A `ClientConnection` owns state, registration, server-connection assignment, the handshake state machine, the write channel, and teardown; a `ClientConnectionManager` holds them keyed by `connectionId`. Transports become genuinely pluggable `IClientTransport` implementations differing only in how bytes get in and out:

| Transport | Read side | Write side | Lifetime |
|---|---|---|---|
| WebSocket | socket receive loop | socket send loop | one request |
| SSE | `POST /{hub}` body → input pipe | `GET /{hub}` response body, `text/event-stream` | GET holds it; `DELETE` ends it |
| Long Polling | `POST /{hub}` body → input pipe | output buffer drained by each `GET /{hub}` | spans many GETs; ends on `DELETE` or timeout |

Two consequences worth stating up front, because they are where this refactor bites:

- **Long Polling establishes the connection on the first GET, before any handshake** — the client POSTs its handshake afterwards. The existing two-phase design (register with `HubProtocol = null`, then `SetProtocolAsync`) already accommodates this; it just gets exercised for real for the first time.
- **SSE and Long Polling have no socket close event.** Disconnect is a timeout: no poll within `DisconnectTimeout` ends the connection and emits `close_connection`. Add `LongPollTimeout` and `DisconnectTimeout` to `SwitchboardOptions`, defaulting to ASP.NET Core's own values (90s poll, 15s disconnect — confirm against `HttpConnectionDispatcherOptions` when implementing rather than trusting this line).

Do this refactor **before** writing either transport, with WebSocket as the only implementation and the Phase 1 tests as the safety net. Adding SSE on top of the current shape means writing it twice.

### D11 — Pattern A trust: the network allowlist must not displace the token

[04-design.md §1](../docs/docs/04-design.md) gives four mandatory rules; the config and the fail-fast startup validation already exist ([Program.cs:23](../src/Keryhe.Switchboard.Server/Program.cs:23)). What is missing is the request-time behavior, and the obvious implementation of "strip identity headers unless the peer is allowlisted" would **break Pattern B** — an app server's trust comes from its `ServerSigningKey` token, not from its network address, and it is under no obligation to sit inside `TrustedProxyNetworks`.

**Recommendation — evaluate in this order, extending D1's dispatch rather than replacing it:**

1. Valid **server** token → step 1, identity headers **trusted** (Pattern B; the trust boundary is the token).
2. Valid **client** token → step 2, identity headers irrelevant.
3. No valid token, `EnableDirectNegotiate == true`, peer inside `TrustedProxyNetworks` → step 1, identity headers **trusted** (Pattern A).
4. No valid token, `EnableDirectNegotiate == true`, peer outside → step 1, identity headers **stripped from the request** and the connection is **anonymous** (not 401 — this is what §1 rule 3 specifies).
5. Otherwise → `401`.

Match on `HttpContext.Connection.RemoteIpAddress`, not `X-Forwarded-For` (§1 rule 4).

> **Call this out in the docs, because rule 4 surprises people:** turning on `EnableDirectNegotiate` lets *anyone* who can reach the negotiate endpoint open an anonymous connection. The allowlist governs whether asserted identity is believed, not whether the endpoint answers. That is the documented design, and it is why the feature is off by default — but the operations guidance should say it in one sentence rather than leaving it to be inferred.

### D12 — Streaming and `CancelInvocation`: verify, don't build

`StreamInvocation` (4), `CancelInvocation` (5), and client→server streams (`streamIds`) are all just hub-protocol frames from the client. The service forwards frames verbatim as `client_message`; the Connector writes them verbatim into the synthetic pipe; `DefaultHubDispatcher` handles them; `StreamItem` (2) and `Completion` (3) come back out through the outbound reader as `send_to_connection`. **No new mechanism is required** — the Phase 1 architecture already covers these deliverables, and the honest Phase 2 work is proving it and fixing what the proof breaks.

One real fix is already visible (finding 7):

**Recommendation: the outbound reader forwards the original frame bytes, not a re-serialization.** Capture the buffer before `TryParseMessage`, and after a successful parse slice off the consumed prefix — `consumed = before.Slice(0, before.Length - after.Length)` — forwarding those bytes verbatim. Parse only to *classify* (drop handshake response, drop `Ping`, detect `Close`), never to re-encode. This removes the `object` round trip for stream items and completions, and is a prerequisite for MessagePack rather than a nicety.

Budget this slice for debugging, not construction. If something here does need a new mechanism, that is a genuine finding for `00-review-findings.md`.

### D13 — The service absorbs client keep-alive pings

Today every client frame — including `Ping` (type 6), which clients send every ~15s — is forwarded to the app server as a `client_message`. At 10,000 clients that is 10,000 envelopes across the server-facing link every 15 seconds carrying no information, and it contradicts [04-design.md §11](../docs/docs/04-design.md), which already states that the service owns client keep-alive for the *outbound* direction (the Connector drops the pipeline's `PingMessage`).

**Recommendation: make it symmetric.** The service absorbs inbound hub-level `Ping` and does not forward it, and owns the client keep-alive timer (`ClientKeepAliveInterval`, already in options).

This is the **one** place the service must look inside a payload. Bound it precisely: read the message `type` field only — for JSON a minimal `Utf8JsonReader` scan, for MessagePack the array header plus the first integer — and forward the original bytes untouched in every non-ping case. Do not let this grow into a message parser; it is a classifier. Same file as D9's `ClientFrameWriter`, same "review failure if widened" note.

---

## 3. Target layout

No new projects. `Keryhe.Switchboard.Orleans` (Phase 3) and `Keryhe.Switchboard.Management` (Phase 4) stay uncreated.

```
src/Keryhe.Switchboard.Protocol/
  Framing/IHubProtocolFraming.cs, JsonFraming.cs, MessagePackFraming.cs   # D8
  ClientFrameWriter.cs                                                     # D9 — handshake/Close/Ping, per protocol
  HubMessageClassifier.cs                                                  # D13 — reads `type` and nothing else
  ServerEnvelope.cs                                                        # + [Key(11)] Payloads (D7)

src/Keryhe.Switchboard.Server/ClientConnections/
  ClientConnection.cs, ClientConnectionManager.cs                          # D10 — lifecycle, request-independent
  Transports/WebSocketClientTransport.cs, SseClientTransport.cs, LongPollingClientTransport.cs
  ClientEndpoints.cs                                                       # GET/POST/DELETE /{hub} dispatch

src/Keryhe.Switchboard.Server/Negotiate/
  DirectNegotiateIdentity.cs                                               # D11 — CIDR match + header stripping

samples/SampleChatApp/
  SampleChatApp.Angular/                                                   # new — the Phase 2 milestone
  js-redirect-check/                                                       # retarget (its README already says how)
```

**Package changes:** `Microsoft.AspNetCore.SignalR.Protocols.MessagePack` **10.0.10** into `Keryhe.Switchboard.Protocol` (D9), and bump `Microsoft.AspNetCore.SignalR.Client` 10.0.0 → 10.0.10 in both test projects (finding 6). The test projects also need the MessagePack protocol package to build MessagePack clients.

---

## 4. Slices

Each slice ends runnable and independently testable. Ordering is deliberate: **groups first** (it is what the sample app visibly needs and it unblocks the failing milestone assertion), **framing prep before MessagePack**, and **the lifecycle refactor before any new transport**.

### Slice 0 — Framing abstraction and frame writers (no behavior change)

- `IHubProtocolFraming` + `Json`/`MessagePack` implementations per **D8**; readers yield frames *including* framing; delete `FrameForServer` and its Phase 1 comment (keep the lesson in `00-review-findings.md`, not duplicated in code).
- `ClientFrameWriter` per **D9**; `HubMessageClassifier` per **D13**.
- MessagePack varint framing per finding 4 — nothing is deserialized.

**Gate:** every Phase 1 test still green with no assertion changes. Byte-pinned tests for both protocols: framing round-trips, adversarial splits (mid-frame, several frames per buffer, a varint prefix split across segments — the MessagePack analogue of the JSON cases Phase 1 already covers), and `ClientFrameWriter` output matching `JsonHubProtocol`/`MessagePackHubProtocol` byte for byte.

### Slice 1 — Groups and users (service-side routing only)

- Real `SendToGroupAsync` / `SendToUserAsync` in `DefaultMessageRouter`, replacing the D3 log-and-no-op. The registry already has the indexes.
- Excluded connection IDs honored on broadcast *and* group sends.
- Partitioned parallel fan-out per [04-design.md §5](../docs/docs/04-design.md): batch, bounded `Parallel.ForEachAsync`, per-connection channel writes that never block the caller.
- **Flip [MilestoneEndToEndTests.cs:117](../tests/Keryhe.Switchboard.IntegrationTests/MilestoneEndToEndTests.cs:117)** from asserting the *absence* of group delivery to asserting delivery. That assertion was written to fail loudly the moment Phase 2 landed; this is that moment.

**Gate:** two clients join a room, a `Clients.Group` send reaches both; an excluded connection does not receive it; `Clients.User` reaches every connection of one user and no other user's; group membership survives a disconnect of an unrelated connection.

### Slice 2 — MessagePack hub protocol (WebSocket only)

- Protocol negotiation from the handshake, using `HandshakeProtocol.TryParseRequestMessage` instead of the hand-rolled JSON parse; accept `json` and `messagepack` at handshake version 1 (finding 3), reject anything else with the standard error frame.
- The handshake stays JSON+`\x1e` (finding 1) but its **frame type follows the negotiated protocol's transfer format** (finding 2) — the response to a MessagePack client is JSON bytes in a Binary frame. `WebSocketClientTransport` stops hardcoding `Text`.
- Per-connection framing everywhere after the handshake, driven by `ClientConnectionState.HubProtocol`.
- Service absorbs inbound `Ping` per **D13**.
- Connector: synthesize the handshake with the connection's actual protocol (already carried on `open_connection`); the outbound reader parses with that protocol and **forwards original bytes** per **D12**.

**Gate:** a .NET `HubConnection` with `.AddMessagePackProtocol()` completes the entire Phase 1 milestone flow — connect, receive the pushed `Connected`, invoke `JoinRoom`/`SendMessage`, get completions back. Same test body as the JSON milestone, parameterized on protocol.

### Slice 3 — Mixed-protocol fan-out (D7)

- `[Key(11)] Payloads` appended to `ServerEnvelope`, with a byte-pinned test proving `[Key(0..10)]` did not move.
- Connector emits `Payloads` (json + messagepack) on `broadcast` / `send_to_group` / `send_to_user`; keeps single `Payload` on `send_to_connection`.
- Service selects the entry matching each target's `HubProtocol`; drops and logs a warning when absent.

**Gate:** one JSON client and one MessagePack client in the same group; a single `Clients.Group(...).SendAsync` is received correctly by both. A `send_to_connection` to each still uses the single-payload path. An envelope with a `Payloads` set missing the target's protocol produces exactly one warning and no write.

### Slice 4 — Connection lifecycle refactor (D10), still WebSocket-only

- Extract `ClientConnection` + `ClientConnectionManager`; `ClientConnectionEndpoint` becomes a thin WebSocket adapter over them.
- `GET /{hub}` dispatches on request shape: WebSocket upgrade vs `Accept: text/event-stream` vs plain GET (long poll). Only the WebSocket branch is live in this slice; the others return `404` until their slice lands.
- `DisconnectTimeout` / `LongPollTimeout` options and the idle reaper, wired but only reachable by the not-yet-written transports.

**Gate:** all of Slices 0–3's tests pass unchanged. This slice is behavior-preserving by definition — any test that has to change is evidence the refactor changed semantics.

### Slice 5 — Server-Sent Events

- `GET /{hub}` with `Accept: text/event-stream` → SSE write side (`data: ` + frame + blank line, per [03-protocol.md §1.5](../docs/docs/03-protocol.md)); `POST /{hub}` → read side; `DELETE /{hub}` → close.
- Text transfer format only — SSE cannot carry MessagePack. `availableTransports` advertises `ServerSentEvents` with `["Text"]` only, and a MessagePack handshake over SSE is rejected with the standard handshake error.
- Advertise `ServerSentEvents` in negotiate **only now that it works** — [DefaultNegotiationService.cs:58](../src/Keryhe.Switchboard.Server/Negotiate/DefaultNegotiationService.cs:58) currently advertises WebSockets alone, and that honesty is worth preserving slice by slice.

**Gate:** a .NET client pinned to `HttpTransportType.ServerSentEvents` runs the full flow including a group message and a server push; killing the GET stream produces `close_connection` within `DisconnectTimeout`.

### Slice 6 — Long Polling

- `GET /{hub}` (no upgrade, no SSE accept) → poll: return buffered messages, or `204` after `LongPollTimeout`; `POST /{hub}` → send; `DELETE /{hub}` → close.
- Output buffering between polls, bounded by `WriteChannelCapacity` with the `DropWrite` policy.
- First-GET-establishes-connection ordering per **D10**; both Text and Binary transfer formats (Long Polling can carry MessagePack).
- Advertise `LongPolling` in negotiate.

**Gate:** a .NET client pinned to `HttpTransportType.LongPolling` runs the full flow over both protocols; messages produced while no poll is outstanding are delivered on the next poll; abandoning polls produces `close_connection` within `DisconnectTimeout`.

### Slice 7 — Streaming, `CancelInvocation`, `Send`/`SendCore` (D12 — a verification slice)

- Server→client streaming (`StreamInvocation` → `StreamItem`* → `Completion`), client→server streaming (`streamIds`), `CancelInvocation`, hub-level `Ping`/`Close`, and the `Send`/`SendCore` variants with and without an invocation ID.
- Add streaming methods to `ChatHub` to have something real to drive.
- Expect to *find* bugs here rather than write features; anything needing a new mechanism goes in `00-review-findings.md`.

**Gate:** streaming tests over both protocols and at least two transports; a cancelled stream stops producing items and completes; a stream that faults surfaces the error in the `Completion`.

### Slice 8 — Pattern A (D11)

- Ordered dispatch per **D11**; CIDR matching against the immediate peer; identity headers **stripped from the request**, not merely ignored, for untrusted callers.
- Startup validation already exists — add a test that pins it.

**Gate:** an allowlisted peer negotiates with asserted identity and gets it; a non-allowlisted peer asserting `X-Switchboard-UserId: admin` gets an **anonymous** connection, and the resulting hub `Context.User` is unauthenticated (this is the Phase 0 `authenticationType` fix earning its keep); Pattern B still works from a peer *outside* the allowlist; `EnableDirectNegotiate = false` still returns 401 for an untokened request.

### Slice 9 — CORS, the Angular sample, and the compatibility matrix

- **`Origin` validation on the WebSocket upgrade.** Browsers do not preflight a WebSocket upgrade, so the CORS middleware does not protect it — when `AllowedOrigins` is non-empty the upgrade handler must check `Origin` itself and reject a mismatch. Easy to assume `app.UseCors` covers this; it does not.
- Verify preflight on `POST /{hub}/negotiate` and on the SSE/Long Polling `POST`/`DELETE` routes.
- `SampleChatApp.Angular` per [08-sample-app.md](../docs/docs/08-sample-app.md), against `SampleChatApp.Api`, with the dev-proxy setup so local development needs no CORS.
- Retarget `samples/SampleChatApp/js-redirect-check/` — its README already specifies exactly what to change (drop the `__diag` assertion, point at `/chatHub`) — and rename it off `phase0-spike-js-client`.
- Integration matrix: {WebSockets, SSE, LongPolling} × {json, messagepack}, skipping the impossible SSE+messagepack cell with an explicit assertion that negotiate advertises it as Text-only.

**Gate:** the Phase 2 milestone — Angular in a real browser, full chat flow (join, send, receive, leave), plus the .NET client green across the matrix.

---

## 5. Testing strategy

Carry Phase 1's discipline forward; three additions specific to this phase:

- **Parameterize, don't duplicate.** The transport × protocol matrix is 6 cells minus 1. Write the flow once as a theory over `(TransportType, HubProtocol)`; a per-transport copy-paste will drift and hide exactly the intersection bugs this phase exists to find.
- **Assert absence, still.** A dropped mixed-protocol payload must be observable (D7). A stripped identity header must produce an *anonymous* principal, not merely a missing claim (D11). An unimplemented transport must not be advertised in negotiate.
- **Real clients remain ground truth.** `Microsoft.AspNetCore.SignalR.Client` pinned per transport and per protocol; `@microsoft/signalr` via the retargeted redirect check and the Angular app. Phase 1's two design-doc defects were both found by tests that exercised a claim rather than reviewed it — the framework facts in §1 were verified the same way, and the remaining assumptions in this plan deserve the same treatment.
- **Bound every async wait.** Long Polling and SSE make this sharper than Phase 1: a hung poll is indistinguishable from a slow one without an explicit timeout.

---

## 6. Deliverable ↔ slice mapping

Every checkbox in [06-project-plan.md § Phase 2](../docs/docs/06-project-plan.md):

| Deliverable | Slice |
|---|---|
| Server-Sent Events transport | 4 (lifecycle), 5 |
| Long Polling transport | 4 (lifecycle), 6 |
| MessagePack hub protocol (length-prefix framing) | 0, 2 |
| Protocol negotiation in the handshake | 2 |
| Group management + fan-out via `SendToGroupAsync` | 1 |
| User targeting + user connection index | 1 |
| Streaming (`StreamInvocation` / `StreamItem` / `Completion`) | 7 |
| `CancelInvocation` handling | 7 |
| Hub-level `Ping` / `Close` (distinct from transport keep-alive) | 2 (inbound ping, D13), 7 |
| `Send` / `SendCore` variants | 7 |
| Excluded connection IDs in broadcast and group sends | 1 |
| CORS verified for browser clients (negotiate preflight, WS `Origin`) | 9 |
| Pattern A — config, header handling, allowlist | 8 |
| `SampleChatApp.Angular` wired to `SampleChatApp.Api` | 9 |
| Integration tests per transport × protocol | 9 (matrix), plus each slice's own gate |

Not on the roadmap's list but required by it: the lifecycle refactor (D10, Slice 4) and the framing inversion (D8, Slice 0) are enabling work, and the mixed-protocol payload set (D7, Slice 3) is a wire-contract change the roadmap does not anticipate — it falls out of "MessagePack" and "group fan-out" both being in scope.

---

## 7. Documentation updates due at the end of Phase 2

- **[03-protocol.md §1.2](../docs/docs/03-protocol.md)** — record that the handshake is always JSON+`\x1e` regardless of protocol, and that the WebSocket frame type follows the negotiated protocol's transfer format (findings 1–2). Neither is currently stated and both are non-obvious.
- **[03-protocol.md §1.3](../docs/docs/03-protocol.md)** — specify the MessagePack varint prefix concretely (7-bit, LSB-first, continuation bit) rather than "VarInt".
- **[03-protocol.md §§1.5–1.6](../docs/docs/03-protocol.md)** — fill in the SSE and Long Polling sections with what was actually implemented: status codes, timeouts, the first-GET-establishes ordering, SSE being Text-only.
- **[03-protocol.md §2.3](../docs/docs/03-protocol.md)** — document `payloads` on the three fan-out envelopes and the single-payload rule for targeted sends (D7).
- **[04-design.md §1](../docs/docs/04-design.md)** — the D11 ordered dispatch, including the "Pattern B trust is the token, not the network" point and the anonymous-connection consequence of enabling Pattern A.
- **[04-design.md §5](../docs/docs/04-design.md)** — group/user fan-out is no longer deferred; describe the batching actually used.
- **[04-design.md §6](../docs/docs/04-design.md)** — replace the three-paragraph transport sketch with the real `ClientConnection`/`IClientTransport` split (D10).
- **[04-design.md §11](../docs/docs/04-design.md)** — the outbound reader forwards original bytes (D12), and inbound ping absorption (D13).
- **[05-data-models.md](../docs/docs/05-data-models.md)** — `[Key(11)] Payloads`; new `SwitchboardOptions` transport timeouts.
- **[08-sample-app.md](../docs/docs/08-sample-app.md)** — Angular now exists; fold the `/api/chatHub` → `/chatHub` correction into the body rather than leaving it as a header note.
- **[06-project-plan.md](../docs/docs/06-project-plan.md)** — tick Phase 2; note what Phase 3 inherits (notably that `IBackplane` is still `NoOpBackplane` and every fan-out path added in Slice 1 is node-local).
- **[00-review-findings.md](../docs/docs/00-review-findings.md)** — a Phase 2 results entry in the same format as Phases 0 and 1.
- **[CLAUDE.md](../CLAUDE.md)** — Project Status, and the architecture notes on transports/protocols that Phase 2 changes.

---

## 8. Risks

| Risk | Mitigation |
|---|---|
| The lifecycle refactor (D10) silently changes WebSocket semantics | Slice 4 is behavior-preserving by definition — it lands with **zero** test changes, and any test needing one is treated as a defect, not a fixture update |
| Mixed-protocol fan-out (D7) is discovered late, after groups and MessagePack are both "done" | It is its own slice with its own gate, placed immediately after the two features that create it, rather than left to the matrix in Slice 9 |
| The framing inversion (D8) reintroduces the Phase 1 stall in a new transport | Frames carry their framing by construction; the byte-pinned tests in Slice 0 run before any transport is written; `ConnectorEndToEndTests` (the minimal `Echo` repro that catches exactly this) is in every slice gate |
| Long Polling flakiness makes CI unreliable | Explicit bounded waits everywhere, no `Thread.Sleep`; poll and disconnect timeouts set low in tests and read from options rather than hardcoded |
| Pattern A ships with the header-stripping rule applied to Pattern B, breaking the app-server path | D11 fixes the evaluation order; the gate includes "Pattern B works from a peer outside the allowlist" as an explicit case |
| `Origin` is assumed to be covered by `UseCors` on the WebSocket upgrade | Called out as its own Slice 9 item with a negative test (disallowed origin is rejected at upgrade) |
| Streaming turns out to need a real mechanism, not just tests (D12) | Slice 7 is scheduled as a debugging slice; if it does, that is a finding for `00-review-findings.md` and a scope conversation, not silent overrun |
| MessagePack package version drift reintroduces the audit findings | Pin 10.0.10 and bump the two test projects off 10.0.0 in the same change; the restore is audit-clean at that combination |
| Phase 2 accidentally implements Phase 3 (cross-node fan-out) because group routing is right there | Slice 1's fan-out is explicitly node-local via `ILocalTransportRegistry`; `IBackplane` stays `NoOpBackplane` and no Slice touches it |

---

## 9. Definition of done

Per [06-project-plan.md § Definition of Done](../docs/docs/06-project-plan.md):

1. All 13 Phase 2 deliverables implemented.
2. All existing tests still pass — including [MilestoneEndToEndTests.cs:117](../tests/Keryhe.Switchboard.IntegrationTests/MilestoneEndToEndTests.cs:117), now flipped to a positive group assertion.
3. Phase 2 integration tests added and passing, covering the transport × protocol matrix.
4. **Milestone:** `SampleChatApp.Angular` in a browser completes the full chat flow through the proxy; the .NET client is green over all three transports.
5. No unresolved TODO/FIXME in new code.
6. Documentation updates in §7 applied.
