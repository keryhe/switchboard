# Phase 5 — Compatibility Testing & Benchmarking: Implementation Plan

**Source of truth:** [06-project-plan.md § Phase 5](../docs/docs/06-project-plan.md), [ADR-005](../docs/docs/07-adr/ADR-005-protocol-compatibility.md), [03-protocol.md](../docs/docs/03-protocol.md), [04-design.md §§8, 11](../docs/docs/04-design.md), [01-overview.md § Non-Goals](../docs/docs/01-overview.md), [00-review-findings.md](../docs/docs/00-review-findings.md).

**Goal.** Validate that existing real-world apps work without modification, and characterize performance limits.

**Milestone check.** Every cell of the compatibility matrix passes or is explicitly recorded as an accepted incompatibility; the benchmark suite produces reproducible numbers for negotiate throughput, routing latency percentiles, fan-out throughput, and memory per connection; a documented tuning guide accompanies the observed limits.

Phases 1–4 were **construction** phases: each added capability, and each ended with a milestone proving that capability worked. Phase 5 is the first **validation** phase, and validation phases have a failure mode construction phases do not — they can pass vacuously. A compatibility matrix that only ever runs the same client SDK the whole existing suite already uses proves nothing new; a benchmark that measures the wrong thing produces confident numbers nobody should act on. The decisions below are mostly about making Phase 5's tests capable of *failing* for the right reasons.

**The single most important thing this plan establishes:** writing it surfaced a real, reproducible compatibility defect — **the .NET 8 SignalR client cannot connect over SSE at all** (finding 1). It is invisible to all 227 existing tests, it has a verified one-line fix, and it is exactly the class of bug ADR-005 exists to catch. Phase 5 is not a formality.

---

## 1. Preconditions — what Phases 1–4 already settled

| Established | Consequence for Phase 5 |
|---|---|
| ADR-005: unmodified SignalR clients are a **hard requirement**, and "the integration test suite must include real Microsoft SignalR client library instances (not mocks) as the ground-truth compatibility check" | Phase 5 is where that consequence is finally discharged across *more than one* client SDK. Today the suite has exactly one: .NET 10 |
| `TransportProtocolMatrixEndToEndTests` already parameterizes {WebSockets, SSE, LongPolling} × {json, messagepack} as a `[Theory]`, with SSE+MessagePack asserted absent rather than skipped | The matrix *shape* exists and is correct. Phase 5 adds SDK rows to it, not a new harness concept (**D30**) |
| `ProcessFixture` spawns real out-of-process `dotnet` servers with SIGTERM stop/restart | The out-of-process pattern non-.NET SDKs need is already in the repo, proven by the Phase 3 milestone |
| `PostgresContainerFixture` / `OtlpCollectorContainerFixture` establish the "throwaway container via the `docker` CLI, `IsAvailable=false` rather than throw" pattern | The same pattern covers any Phase 5 toolchain that may be absent (Java, in particular — finding 5) |
| Phase 4 shipped `signalr.message.inbound_duration` / `outbound_duration` histograms and OTLP export (**D25**) | Routing-latency percentiles are already instrumented **inside the service**. The load test reads them; it does not re-instrument (**D33**) |
| Phase 4 shipped `signalr.client_connections.active`, `broadcast.fan_out_size`, `messages.routed` | The load test's own observations have a cross-check that does not share their measurement code |
| `Directory.Build.props` sets `TreatWarningsAsErrors` for `src/` **only** — `tests/` has none | Phase 5 test projects pinning older client SDKs will not fail restore on advisories, but see finding 6 — that is a reason to pin deliberately, not to ignore them |
| Known, documented Connector incompatibilities: `Context.GetHttpContext()` is always `null`; a custom `IUserIdProvider` diverges from the service's user index | Both are already in the [risk register](../docs/docs/06-project-plan.md) *"add to the Phase 5 compatibility matrix"*. **D31** makes them assertions, not prose |
| Non-goals: stateful reconnect, message replay, Azure API parity | The matrix must assert these **degrade gracefully**, not that they work — a client requesting stateful reconnect must still connect (ADR-005 says so explicitly) |

### New facts verified while writing this plan

Every item below was checked empirically against this repository at its Phase 4 state — real processes, real client SDKs, real sockets — not inferred from the docs. Each contradicts something a reasonable implementer would otherwise assume.

1. **The .NET 8 SignalR client cannot connect over SSE. At all. This is a live bug.** Verified end-to-end: a real `net8.0` `HubConnection` (`Microsoft.AspNetCore.SignalR.Client` 8.0.29) pinned to `HttpTransportType.ServerSentEvents`, against a real `Keryhe.Switchboard.Server` + real `SampleChatApp.Api`, fails during `StartAsync` with:

   ```
   System.FormatException: Unexpected '\n' in message. A '\n' character can only be
   used as part of the newline sequence '\r\n'
      at ServerSentEventsMessageParser.ParseMessage(...)
      at ServerSentEventsTransport.ProcessEventStream(...)
      at HubConnection.HandshakeAsync(...)
   ```

   **Cause:** [SseClientEndpoint.cs:116](../src/Keryhe.Switchboard.Server/ClientConnections/SseClientEndpoint.cs:116) terminates each event with a bare `"\n\n"`. The .NET 8 client's SSE parser requires `\r\n`; the .NET 10 client's does not.

   **Blast radius, isolated by testing each client against the unpatched service:** .NET 8 **fails**; .NET 10 **passes**; JS `@microsoft/signalr` 8.0.17 **passes**. So it is precisely one SDK — and it fails 100% of the time, not intermittently.

   **Fix verified:** changing that one write to `"\r\n\r\n"` makes .NET 8 pass while .NET 10 and JS 8.0.17 continue to pass (all three re-run against the patched service). CRLF is what [03-protocol.md §1.5](../docs/docs/03-protocol.md) should specify and what every SSE consumer accepts. *(Change reverted; it belongs in Slice 1.)*

   **Why 227 tests missed it:** every SSE test in the suite drives a .NET **10** client. This is the strongest possible argument for **D30** — a matrix that varies only transport and protocol, holding the SDK fixed, cannot find SDK-specific defects by construction.

2. **`SwitchboardHubLifetimeManager<THub>` does not implement client results, and the failure is a hard `NotImplementedException`.** `HubLifetimeManager<THub>` in .NET 10 has three **virtual** members beyond the 13 abstract ones: `InvokeConnectionAsync`, `SetConnectionResultAsync`, `TryGetReturnType`. The Connector overrides none of them. Verified by calling the base implementations on a subclass that overrides only the abstract members — exactly the Connector's shape:

   ```
   InvokeConnectionAsync   → System.NotImplementedException: <T> does not support client return values.
   SetConnectionResultAsync → System.NotImplementedException: <T> does not support client return values.
   TryGetReturnType         → returns false (benign)
   ```

   So hub code calling `await Clients.Client(id).InvokeAsync<int>("GetValue")` — a first-class SignalR feature since .NET 8 — throws. **Client results appear nowhere in this project's documentation**: not in [01-overview.md's non-goals](../docs/docs/01-overview.md), not in [ADR-005's "What Is Not In Scope"](../docs/docs/07-adr/ADR-005-protocol-compatibility.md) (which does list stateful reconnect), not in [04-design.md](../docs/docs/04-design.md). It is an undocumented gap, not a recorded decision — see **D32**.

3. **`SampleChatApp.Api` cannot serve MessagePack clients**, because it calls `builder.Services.AddSignalR()` without `.AddMessagePackProtocol()` ([Program.cs:40](../samples/SampleChatApp/SampleChatApp.Api/Program.cs:40)). Verified: a real .NET 8 *and* .NET 10 MessagePack client both fail against it, while both succeed over JSON. This is correct SignalR behavior — `HubConnectionHandler`'s `IHubProtocolResolver` only knows protocols the app server registered, exactly as `ConnectorEndToEndTests` already documents in a code comment — but it means **the designated end-to-end compatibility target can only exercise half the matrix**. One line in the sample fixes it; without it, every MessagePack row of Slice 1 is untestable against the sample.

4. **The existing suite has never exercised the real Connector over MessagePack out-of-process.** `TransportProtocolMatrixEndToEndTests` covers MessagePack thoroughly — but against `AppServerDouble`, a hand-rolled envelope speaker, not `Keryhe.Switchboard.Connector`. `ConnectorEndToEndTests` covers the real Connector with MessagePack, but in-process. `MilestoneEndToEndTests` (out-of-process, real Connector) is JSON-only. The intersection — **real Connector + MessagePack + out-of-process** — is untested, and finding 3 is why: the sample app it would run against cannot do MessagePack.

5. **The Java client row has two hard toolchain obstacles.** Verified on this machine: Java 17 is installed, but **neither `mvn` nor `gradle` is present**. And on Maven Central, `com.microsoft.signalr:signalr` has **no stable 10.x** — the newest stable releases are `9.0.6` and `8.0.17`; 10.x exists only as `10.0.0-preview.5`. So the Java row cannot mirror the .NET rows' versioning, and it needs either a build tool installed or vendored jars. See **D34**.

6. **Pinning old client SDKs pulls in packages with live advisories — but patched versions exist.** `Microsoft.AspNetCore.SignalR.Protocols.MessagePack` **8.0.17** carries a known high-severity advisory (GHSA-f8h2-vmm9-qhj6). Patched 8.0.x releases are published through **8.0.29**. Since `tests/` has no `TreatWarningsAsErrors`, an unpatched pin would emit `NU1903` as a warning and build anyway — silently shipping a known-vulnerable test dependency. Pin `8.0.29`, not `8.0.17`.

7. **BenchmarkDotNet 0.15.8 is conflict-free here, and is the wrong tool for four of the five things the roadmap asks it to measure.** Verified: BDN 0.15.8 added to a project referencing the real `Core` + `Protocol` projects restores and builds at `0 Warning(s), 0 Error(s)`. But BDN is an in-process microbenchmark harness — tight iteration loops, statistical outlier rejection, single-threaded by default. "Negotiate throughput (connections/sec)", "broadcast fan-out throughput (messages/sec × connection count)", and "10,000 simulated clients" are **concurrent load** questions; BDN measures neither concurrency nor sustained throughput. It is genuinely excellent for the per-operation CPU/allocation costs on the hot path. See **D33** — this is a roadmap doc correction in the same category as Phase 4's D25.

8. **A single-machine 10,000-client load test is feasible on this host but sits close to a hard ceiling.** `ulimit -n` is 1,048,576 (not a constraint), but the macOS ephemeral port range is `net.inet.ip.portrange.first=49152` → `last=65535` — **16,384 ports total**. Every loopback client connection consumes one, and the service's own outbound connections (app-server pool, Orleans silo-to-silo, OTLP exporter) draw from the same pool. 10,000 clients fits with ~6,000 spare — but `TIME_WAIT` accumulation across back-to-back runs can exhaust it, producing a failure that looks like a service defect and is not. The load harness must report port-exhaustion distinctly (**D35**).

9. **`js-redirect-check` is not automated by anything.** `grep` across `tests/`, all `*.csproj`, and every shell/CI file: the only reference to it is its own `package.json` script entry. CLAUDE.md describes it as "retargeted at the real `SampleChatApp.Api` and no longer parked for later," which is true of its *target* but not of its *execution* — it is a manual script that checks only the negotiate redirect, not a matrix cell. Separately, the JS versions in the repo have drifted apart accidentally: `js-redirect-check` pins `@microsoft/signalr` **^8.0.7** while `SampleChatApp.Angular` uses **^10.0.0**. Both are legitimate targets; neither was chosen deliberately.

10. **JS MessagePack needs a second npm package.** `@microsoft/signalr-protocol-msgpack` (10.0.0 current) is separate from `@microsoft/signalr`, and `SampleChatApp.Angular` does not reference it — so the JS rows are JSON-only today, and the MessagePack JS cells need that dependency added to whatever harness runs them.

---

## 2. Decisions

Seven decisions, **D30–D36**, continuing D1–D6 (Phase 1), D7–D13 (Phase 2), D14–D20 (Phase 3), and D21–D29 (Phase 4), so a code comment saying "plan decision D25" stays unambiguous.

### D30 — The compatibility matrix varies the **SDK**, and every non-.NET SDK runs out-of-process against one shared harness contract

Finding 1 is the entire argument: 227 tests, a correctly-parameterized transport × protocol matrix, and a 100%-reproducible SSE failure sat undetected because every one of those tests instantiates the same .NET 10 client. The matrix's missing axis is the SDK, and it is the axis that actually finds bugs.

**Recommendation.** Define one **client-probe contract** — a small executable that takes `(baseUrl, transport, protocol)`, performs a fixed scenario (connect → receive caller push → join group → invoke hub method → receive group message → clean disconnect), and prints a single machine-readable `RESULT OK …` / `RESULT FAIL …` line. Implement it once per SDK:

| SDK | Runtime | How it runs | Version pin |
|---|---|---|---|
| .NET 10 | in-process | keep the existing `[Theory]` — it is already correct | 10.0.0 |
| .NET 8 | out-of-process `net8.0` exe | `ProcessFixture`-style spawn | 8.0.29 (finding 6) |
| JavaScript | out-of-process `node` | spawn `node probe.mjs` | 8.0.17 **and** 10.0.0 (finding 9) |
| Java | out-of-process `java` | see **D34** | 9.0.6 stable (finding 5) |

An xunit `[Theory]` supplies the cells; the test asserts on the probe's exit code and `RESULT` line. This deliberately reuses `ProcessFixture`'s existing shape rather than inventing a second out-of-process mechanism — and the probe contract is what keeps four language implementations honest about running *the same scenario*.

**The .NET 8 row must include SSE specifically**, and it must be written so that it fails before Slice 1's fix lands and passes after — the regression-reproduction discipline Phases 1–4 used, applied to a bug found before the phase started.

### D31 — Documented incompatibilities become **executable assertions**, not prose

Three known incompatibilities are currently prose in a risk register or a design-doc note: `Context.GetHttpContext()` returns `null`; a custom `IUserIdProvider` diverges from the service's user index; stateful reconnect falls back rather than working. Prose does not fail a build when the behavior silently changes — and "silently changes" is precisely how a documented-and-accepted incompatibility turns into an undocumented regression.

**Recommendation.** Each gets a test that asserts the *documented* behavior:

- A hub that reads `Context.GetHttpContext()` sees `null` — asserted, so the day someone adds an `IHttpContextFeature` to the synthetic connection, the doc and the code disagree loudly.
- A client calling `.WithStatefulReconnect()` **connects successfully** and behaves as a standard reconnect — ADR-005's explicit promise ("the connection is not broken"), never actually tested.
- A custom `IUserIdProvider` on the app server, and what `Clients.User(...)` does — assert the real behavior and record it, whichever way it lands.

These are assertions of *current, intended* behavior. Where an assertion is unpleasant, the fix is a doc change or a code change — decided in the slice, not pre-judged here.

### D32 — Client results (`InvokeConnectionAsync`) is a **decision to make**, not a bug to quietly fix

Finding 2: hub code calling `Clients.Client(id).InvokeAsync<T>(...)` throws `NotImplementedException`, and nothing in the documentation says it should. Three options, and the plan should not pretend this is a trivial call:

1. **Implement it.** The client's `CompletionMessage` already flows back over the existing `client_message` path to the assigned app server. But correctness under scale-out is the hard part: the invoking app server and the client's assigned server connection can be different processes (**D18**), so the completion can arrive at an app server that has no pending invocation for it. Doing this properly means routing completions by `invocationId` to the *originating* app server — plausibly a new `ServerEnvelope` field and a cross-node correlation path. That is Phase-3-sized work, not a Phase 5 afterthought.
2. **Declare it a non-goal**, in [01-overview.md](../docs/docs/01-overview.md) and [ADR-005](../docs/docs/07-adr/ADR-005-protocol-compatibility.md), beside stateful reconnect — which was excluded for a structurally similar reason (per-connection state that does not survive the proxy topology).
3. **Improve the failure.** The `NotImplementedException` surfaces as an opaque framework error; overriding the two members to throw a `NotSupportedException` naming Switchboard and pointing at the docs costs ~5 lines.

**Recommendation: (2) + (3) for Phase 5, with (1) recorded as a candidate future enhancement** — mirroring exactly how stateful reconnect was handled. Rationale: Phase 5's charter is *validation*, and a feature this size landing inside a validation phase is how validation phases stop finishing. But the decision belongs to the reader of this plan; if client results are a hard requirement for the intended audience, it is a Phase 6, not a Slice 2 bullet.

Either way the matrix gets a test pinning the chosen behavior, so it is never again *undocumented*.

### D33 — Split "benchmarking" into microbenchmarks and load tests; take latency percentiles from the service's own Phase 4 histograms

Finding 7: BenchmarkDotNet cannot measure four of the five things the roadmap points it at. The roadmap conflates two different questions — "how expensive is this operation" and "what happens under concurrent load" — and only the first is a BDN question. **This is a roadmap doc correction, in the same category as Phase 4's D25.**

**Recommendation — two separate deliverables:**

**`Keryhe.Switchboard.Benchmarks`** (BenchmarkDotNet 0.15.8, `MemoryDiagnoser` on): the hot-path per-operation costs, all in-process, no sockets —
- `ServerEnvelopeSerializer` write/read round-trip (the server-facing wire format, on every message)
- `JsonFrameProtocol.TryParseFrame` / `MessagePackFraming` frame parsing
- `HubMessageClassifier.IsPing` (runs on every inbound client frame — **D13**)
- `DefaultMessageRouter` fan-out over an in-memory `ILocalTransportRegistry` at 1/100/1k/10k local targets, `DropWrite` channels drained — isolates routing cost from socket cost
- `ManagementInvocationWriter` argument mapping (**D22**)

**A load-generator harness** (a plain console app, not BDN) for the concurrent questions: negotiate throughput, sustained fan-out throughput, connection ramp, and memory per connection (RSS delta ÷ connection count at plateau).

**Latency percentiles come from the service, not the harness.** Phase 4 already ships `signalr.message.inbound_duration` / `outbound_duration` as histograms with OTLP export (**D25**), which is exactly P50/P95/P99 of the service's own routing cost — the number the roadmap asks for, already instrumented, already tested. The harness reads them (or the collector does) rather than re-deriving latency from client-side timestamps that also include client scheduling noise. The harness's own end-to-end timings stay as a **cross-check** on a number it did not produce.

### D34 — The Java row is real but toolchain-gated, and it degrades the same way the database and collector fixtures do

Finding 5: no `mvn`, no `gradle`, and no stable 10.x Java client. The honest options are (a) require a build tool, (b) vendor jars and invoke `java` directly, or (c) run it in a container.

**Recommendation: (c), reusing the pattern already proven twice.** `PostgresContainerFixture` and `OtlpCollectorContainerFixture` both spin a throwaway container via the `docker` CLI and set `IsAvailable = false` with an explanatory `UnavailableReason` when Docker is absent — the owning test then no-ops with a message *rather than failing the suite*, because [Phase 3's plan](phase-3-scale-out-and-resilience.md) is explicit that an unavailable dependency must be **called out as untested, not assumed working**. A `maven:3-eclipse-temurin-17` container building and running the Java probe fits that pattern exactly, needs nothing installed on the host, and pins the JDK.

**Pin `9.0.6`** (newest stable) and record in the compatibility doc that no stable 10.x Java client exists — that is a fact about the ecosystem the matrix should state, not paper over.

If Docker is unavailable *and* the Java row therefore never runs, the deliverable is a matrix that says so explicitly. A row marked "untested — no toolchain" is worth more than a row silently marked green.

### D35 — The load harness must distinguish *its own* limits from the service's

Finding 8: 10,000 loopback clients consume 10,000 of 16,384 available ephemeral ports, shared with the service's own outbound connections, and `TIME_WAIT` from a prior run can exhaust the range. A harness that reports "connection failures at 9,400 clients" without distinguishing *why* will be read as a service limit and will be wrong.

**Recommendation.** The harness classifies every connection failure by cause and reports the classes separately: ephemeral-port exhaustion (`EADDRNOTAVAIL`/`AddressNotAvailable`), file-descriptor exhaustion, negotiate `503` (the service's own **D5** backpressure — a *correct* response, not a failure), handshake timeout, and everything else. Only the last two are candidate service defects.

It also records the host's actual limits (`ulimit -n`, the ephemeral port range) into its output, so a number from one machine can be compared honestly against a number from another. **The tuning guide (§ Slice 5) is written from these observations**, not from general advice — that is what makes it worth having.

### D36 — Every compatibility result lands in a generated document, and "untested" is a first-class result

The deliverable is not "the tests pass"; it is a matrix an adopter can read to decide whether their app will work. A green CI run does not answer "does the Java client support MessagePack over Long Polling against this service?"

**Recommendation.** The matrix suite emits a Markdown table into `docs/docs/11-compatibility-matrix.md` with exactly three states per cell — **pass**, **not applicable** (SSE + MessagePack, which the service correctly refuses by design), and **untested** (toolchain unavailable, per **D34**) — plus a separate "known incompatibilities" section fed by **D31**/**D32**. A failing cell fails the build; it never reaches the document as a fourth state.

---

## 3. Target layout

```
tests/Keryhe.Switchboard.CompatibilityTests/        # new — the D30 matrix host (xunit)
  ClientProbeContract.md                            # the one scenario every SDK probe implements
  ProbeRunner.cs                                    # spawn + parse RESULT line (reuses ProcessFixture's shape)
  CompatibilityMatrixTests.cs                       # [Theory] over SDK × transport × protocol
  KnownIncompatibilityTests.cs                      # D31: GetHttpContext null, stateful reconnect, IUserIdProvider
  ClientResultsTests.cs                             # D32: pins whichever behavior is chosen
  MatrixDocumentWriter.cs                           # D36: emits 11-compatibility-matrix.md
  JavaClientContainerFixture.cs                     # D34: maven container, IsAvailable=false when absent

tests/clients/                                      # new — one probe per SDK, same contract
  dotnet8/                                          # net8.0 console, SignalR.Client 8.0.29 (finding 6)
  js/                                               # node, @microsoft/signalr 8.0.17 + 10.0.0 + msgpack (findings 9, 10)
  java/                                             # maven project, com.microsoft.signalr 9.0.6 (finding 5)

tests/Keryhe.Switchboard.Benchmarks/                # new — BenchmarkDotNet 0.15.8, microbenchmarks only (D33)
  EnvelopeSerializationBenchmarks.cs
  FrameParsingBenchmarks.cs
  FanOutBenchmarks.cs
  ManagementInvocationWriterBenchmarks.cs

tests/Keryhe.Switchboard.LoadHarness/               # new — console app, concurrent load (D33)
  ConnectionRamp.cs                                 # negotiate throughput, connection ramp
  FanOutLoad.cs                                     # sustained broadcast throughput
  FailureClassifier.cs                              # D35 — port/fd exhaustion vs. real service failure
  HostLimitsReport.cs                               # D35 — ulimit -n, ephemeral port range

src/Keryhe.Switchboard.Server/ClientConnections/
  SseClientEndpoint.cs                              # finding 1 — "\n\n" → "\r\n\r\n" (Slice 1)

src/Keryhe.Switchboard.Connector/
  SwitchboardHubLifetimeManager.cs                  # D32 — clearer NotSupportedException (if option 3 chosen)

samples/SampleChatApp/SampleChatApp.Api/
  Program.cs                                        # finding 3 — .AddMessagePackProtocol()

docs/docs/11-compatibility-matrix.md                # new — generated, D36
docs/docs/12-performance.md                         # new — observed limits + tuning guide
```

**Package changes:** `BenchmarkDotNet` **0.15.8** (verified conflict-free, finding 7) in the benchmarks project only. `Microsoft.AspNetCore.SignalR.Client` / `.Protocols.MessagePack` **8.0.29** (not 8.0.17 — finding 6) in the .NET 8 probe. `@microsoft/signalr` 8.0.17 and 10.0.0 plus `@microsoft/signalr-protocol-msgpack` 10.0.0 in the JS probe (finding 10). `com.microsoft.signalr:signalr:9.0.6` in the Java probe (finding 5). No new dependency in any `src/` project.

---

## 4. Slices

Ordering puts the **known-broken** cell first: Slice 1 fixes a defect that already exists in shipped code, and everything after it is discovery.

### Slice 1 — The .NET 8 row, and the SSE fix it already found

- `"\n\n"` → `"\r\n\r\n"` in `SseClientEndpoint.WriteOutputAsync` (finding 1); correct [03-protocol.md §1.5](../docs/docs/03-protocol.md) to specify CRLF.
- The probe contract (**D30**) + `ProbeRunner`, with the `net8.0` probe as its first implementation.
- `.AddMessagePackProtocol()` in `SampleChatApp.Api` (finding 3), unblocking every MessagePack cell.

**Gate:** the .NET 8 probe passes all five valid transport × protocol cells against a real out-of-process service + `SampleChatApp.Api`. **The SSE cell must be demonstrated failing before the fix and passing after** — the same reproduce-then-fix discipline Phases 1–4 used, and the reason this slice is first. The existing 227 tests still pass unchanged (the .NET 10 client tolerates CRLF — verified). MessagePack over the real Connector, out-of-process, now runs at all (finding 4).

### Slice 2 — JavaScript and Java rows

- JS probe against `@microsoft/signalr` **8.0.17 and 10.0.0** (finding 9), adding `@microsoft/signalr-protocol-msgpack` for the MessagePack cells (finding 10).
- Java probe in a `maven` container (**D34**), pinned to stable `9.0.6` (finding 5).
- Retire or absorb `js-redirect-check` — it is a manual script nothing runs (finding 9); its negotiate-redirect check is a strict subset of the probe scenario.

**Gate:** both JS versions pass every applicable cell. The Java row passes, or is recorded `untested` with its `UnavailableReason` when Docker is absent — never silently green (**D34**). Any newly-discovered SDK-specific defect gets the Slice 1 treatment: reproduce, fix, pin.

### Slice 3 — Known incompatibilities and the client-results decision

- **D31** assertions: `GetHttpContext()` is `null`; `.WithStatefulReconnect()` connects and degrades to standard reconnect; custom `IUserIdProvider` behavior pinned as observed.
- **D32**: take the decision, then implement it — non-goal documentation + a clearer `NotSupportedException`, or full client-results support if that is the call.
- Verify `AddSwitchboardConnector()` against `AddAzureSignalR()`'s contract: all 13 abstract `HubLifetimeManager` members implemented (they are), the 3 virtuals accounted for explicitly (finding 2), and the app-server-side API shape documented as a package swap per ADR-005.

**Gate:** each documented incompatibility has a test that fails if the behavior changes. Client results either work or throw a Switchboard-specific error naming the limitation — never a bare framework `NotImplementedException`. `docs/docs/11-compatibility-matrix.md` is generated and complete (**D36**).

### Slice 4 — Microbenchmarks (**D33**)

- `Keryhe.Switchboard.Benchmarks` with the five hot-path suites, `MemoryDiagnoser` enabled.
- Fan-out benchmarked at 1 / 100 / 1k / 10k local targets to expose the shape of the curve, not one number.

**Gate:** `dotnet run -c Release` produces a full BDN report; allocations-per-message on the routing path are recorded as a baseline future phases can regress against. No benchmark opens a socket — anything that wants one belongs in Slice 5 by construction.

### Slice 5 — Load harness, observed limits, tuning guide (**D33**, **D35**)

- The load harness: connection ramp, negotiate throughput, sustained fan-out, memory per connection.
- Failure classification (**D35**) and a host-limits report emitted alongside every run.
- Latency percentiles read from the service's own `message.inbound_duration`/`outbound_duration` histograms (**D25**), cross-checked against the harness's independent end-to-end timings.
- `docs/docs/12-performance.md`: observed numbers, the host they came from, and the tuning guide written *from* them.

**Gate:** the harness reaches **10,000 concurrent clients** against a real service, or reports precisely which limit stopped it and whether that limit was the host's or the service's (**D35**) — a run that stops at 9,000 on ephemeral ports is a **pass with a documented host limit**, not a service failure. Service-reported P50/P95/P99 and harness-observed end-to-end latency are reported side by side; a large divergence is itself a finding. Memory per connection is a real measurement, not an estimate.

### Slice 6 — Documentation and phase close-out

- The §7 documentation updates.
- `00-review-findings.md` Phase 5 entry in the established format, including every defect the matrix found.
- `CLAUDE.md` Project Status, solution layout, and a note on the SSE/CRLF class of bug.

---

## 5. Testing strategy

Phases 2–4 discipline carries forward (real clients as ground truth, assert absence, bound every wait, both `UseOrleansCluster` modes). Four additions specific to a validation phase:

- **A validation phase must be able to fail.** Before Slice 1's fix lands, the .NET 8 SSE cell must be *seen* failing. Any cell that has never been observed red is not yet evidence of anything.
- **Every SDK runs the same scenario.** The probe contract is what makes four language implementations comparable; a JS probe that quietly skips the group-message step turns a red cell green for the wrong reason.
- **"Untested" is a result, not a gap to hide.** Toolchain-gated rows (**D34**) report themselves, following the precedent Phase 3 Slice 6 set for an unavailable database.
- **Benchmarks are baselines, not gates.** A microbenchmark that fails CI on a 5% regression on shared hardware trains people to ignore CI. Record baselines; investigate movement deliberately.

---

## 6. Deliverable ↔ slice mapping

Every checkbox in [06-project-plan.md § Phase 5](../docs/docs/06-project-plan.md):

| Deliverable | Slice |
|---|---|
| Compatibility matrix: each SDK × transport × protocol (.NET 8, .NET 10, JS, Java) | 1 (.NET 8; .NET 10 already exists), 2 (JS, Java) |
| End-to-end test of `SampleChatApp.Angular` + `SampleChatApp.Api` through the proxy | 2 — the JS probe *is* the Angular sample's client stack, exercised headlessly |
| Verify `AddSwitchboardConnector()` is a drop-in for `AddAzureSignalR()` | 3 — including the three un-overridden virtuals (finding 2) |
| Benchmark suite using BenchmarkDotNet (4 measurements) | 4 (per-operation costs) + 5 (throughput/latency/memory) — **split, see D33** |
| Load test: 10,000 simulated clients | 5 — with **D35**'s failure classification |
| Document observed limits and Kestrel/OS tuning | 5 |

Not on the roadmap's list but required by it: the SSE CRLF fix (finding 1 — without it the .NET 8 row cannot pass at all); `.AddMessagePackProtocol()` in the sample (finding 3 — without it every MessagePack cell is untestable end-to-end); the client-results decision (**D32**, finding 2 — an undocumented gap the matrix will hit immediately); and the generated matrix document (**D36**), without which the phase produces passing tests but no artifact an adopter can read.

---

## 7. Documentation updates due at the end of Phase 5

- **[06-project-plan.md](../docs/docs/06-project-plan.md)** — tick Phase 5; record the **D33** benchmark split; pin BenchmarkDotNet 0.15.8; note what a future phase inherits.
- **[03-protocol.md §1.5](../docs/docs/03-protocol.md)** — SSE events terminate with **CRLF**, not LF (finding 1), with the .NET 8 parser as the stated reason.
- **[ADR-005](../docs/docs/07-adr/ADR-005-protocol-compatibility.md)** — client results under "What Is Not In Scope" if **D32** lands that way; point at the generated matrix as the compatibility evidence the ADR's own consequences section demands.
- **[01-overview.md](../docs/docs/01-overview.md)** — client results in Non-Goals (if **D32** option 2), beside stateful reconnect.
- **[04-design.md](../docs/docs/04-design.md)** — a §14 recording the Connector's `HubLifetimeManager` coverage: 13 abstract members implemented, 3 virtuals and their disposition.
- **New: [11-compatibility-matrix.md](../docs/docs/11-compatibility-matrix.md)** — generated (**D36**).
- **New: [12-performance.md](../docs/docs/12-performance.md)** — observed limits and tuning guide.
- **[00-review-findings.md](../docs/docs/00-review-findings.md)** — Phase 5 results in the Phase 0–4 format, including the SSE defect and its blast radius.
- **[CLAUDE.md](../CLAUDE.md)** — Project Status, solution layout, and the SSE/CRLF lesson (an entire client SDK broken by a two-character framing difference, invisible to a 227-test suite that varied everything except the SDK).

---

## 8. Risks

| Risk | Mitigation |
|---|---|
| The matrix finds more defects than Phase 5 has room to fix | Slice ordering front-loads the one already found; each new defect gets triaged the same way — fix if small (like finding 1), record as a documented incompatibility (**D31**) if not, and never silently green |
| Client results turns into an unplanned Phase-3-sized project inside a validation phase | **D32** makes it an explicit decision with a recommended answer (document + clear error) and records full support as a future candidate, exactly as stateful reconnect was handled |
| Java row silently never runs and the matrix claims coverage it does not have | **D34** + **D36**: `untested` is a first-class cell state carrying its `UnavailableReason`, following Phase 3 Slice 6's precedent |
| Benchmarks measure the wrong thing and produce confident, useless numbers | **D33** splits microbenchmarks from load tests on the grounds of what BDN actually does (finding 7); latency comes from the service's own Phase 4 histograms rather than being re-derived |
| The load test's own host limits get read as service limits | **D35**: every failure is classified, host limits are recorded in the output, and negotiate `503` is counted as correct backpressure (**D5**), not failure |
| A probe silently skips scenario steps, turning a cell green for the wrong reason | One written probe contract (**D30**) all four SDKs implement; the `RESULT` line reports which steps ran |
| Old client SDK pins ship known-vulnerable packages into the repo | Finding 6: pin **8.0.29**, not 8.0.17; `tests/` lacking `TreatWarningsAsErrors` means this must be deliberate, since restore will not stop it |
| Phase 5 "passes" without proving anything, the classic validation-phase failure | §5's first rule: the .NET 8 SSE cell must be observed failing before it is observed passing |

---

## 9. Definition of done

Per [06-project-plan.md § Definition of Done](../docs/docs/06-project-plan.md):

1. All 6 Phase 5 deliverables implemented, with **D32**'s and **D33**'s corrections applied and recorded, not silently substituted.
2. All existing tests still pass — the Phase 4 baseline of 227 unit + 3 integration tests — in **both** `UseOrleansCluster` modes.
3. Phase 5 tests added and passing: the full SDK × transport × protocol matrix, the **D31** incompatibility assertions, and the **D32** client-results pin.
4. **Milestone:** every matrix cell is pass / not-applicable / untested-with-reason; the benchmark suite and load harness produce reproducible numbers; `11-compatibility-matrix.md` and `12-performance.md` exist and are generated from real runs.
5. No unresolved TODO/FIXME in new code.
6. Documentation updates in §7 applied.
