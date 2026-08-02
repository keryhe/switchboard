# Observed Limits and Tuning Guide

**Generated from one real run, not general advice** — plan decision D33/D35 ([plans/phase-5-compatibility-testing-and-benchmarking.md](../../plans/phase-5-compatibility-testing-and-benchmarking.md)). Produced by `tests/Keryhe.Switchboard.LoadHarness` against a real out-of-process `Keryhe.Switchboard.Server` + `SampleChatApp.Api` pair on a single machine. Every number below came from that run; none are estimates.

Last generated: 2026-08-02 04:36:25 UTC

## Host this run came from

- `ulimit -n` (max open files): **10240**
- Ephemeral port range: **49152–65535** (16384 ports total)

A number from this document is only comparable to a number from a different machine if both carry these limits — see plan decision D35's finding 8: the ephemeral port range, not `ulimit -n`, is usually what a large connection ramp hits first, since it's shared with the service's own outbound connections (app-server pool, Orleans silo-to-silo, OTLP exporter).

## Connection ramp

| Metric | Value |
|---|---|
| Requested | 10,000 |
| Connected | 3,336 |
| Stop reason | **host limit** — 25 consecutive FileDescriptorExhaustion failures — this is the harness's own host running out of a resource, not the service (Too many open files in system (127.0.0.1:52915)) |
| Duration | 3.1 s |
| Negotiate throughput | 1,070.8 connections/sec |

Failures, classified by cause (plan decision D35) — only `HandshakeTimeout` and `Other` are candidate service defects; everything else is either the host's own ceiling or the service's documented, correct backpressure:

| Category | Count |
|---|---|
| FileDescriptorExhaustion | 52 |

## Memory per connection

- Baseline RSS (service process, before ramp): 132.8 MB
- Plateau RSS (service process, after ramp of 3,336): 497.4 MB
- Delta: 364.6 MB
- **Memory per connection: 111.9 KB** (RSS delta ÷ connection count, a real measurement of the service process, not an estimate)

## Sustained fan-out

| Metric | Value |
|---|---|
| Targets | 3,336 |
| Delivered | 3,336 (all) |
| Time to full delivery (or timeout) | 71 ms |
| Throughput | 46,844 messages/sec |

Harness-observed end-to-end latency (client-side: send timestamp → receive timestamp), independent of the service's own instrumentation:

| Percentile | Harness-observed | Service-reported (`outbound_duration`) |
|---|---|---|
| P50 | 55.0 ms | 2.5 ms |
| P95 | 68.4 ms | 4.8 ms |
| P99 | 69.5 ms | 5.0 ms |

**Divergence at P95: 63.6 ms.** Per plan §4, a large divergence between the harness's own end-to-end timing and the service's own routing-cost histogram is itself a finding, not something to reconcile by construction — the harness's timing includes client scheduling, network, and SignalR client deserialization overhead the service's `outbound_duration` histogram was deliberately designed to exclude ([04-design.md §13](../04-design.md#13-observability-phase-4)), so some divergence is expected; investigate only if it's large relative to the absolute latency.

## Tuning guide (written from the numbers above)

This run stopped at **3,336** connections because of a **host** limit, not a service limit: 25 consecutive FileDescriptorExhaustion failures — this is the harness's own host running out of a resource, not the service (Too many open files in system (127.0.0.1:52915))

To push past this on the same machine:

- Raise `ulimit -n` (currently 10240) — each client connection and each of the service's own outbound connections (app-server pool, Orleans silo-to-silo if clustered, OTLP exporter) holds a file descriptor.
- The ephemeral port range (currently 49152–65535, 16384 ports) is shared by every loopback connection this machine makes during the run, including the harness's own HTTP client and the service's outbound connections — widen it (macOS: `sudo sysctl -w net.inet.ip.portrange.first=32768`) or run client and service on separate machines so they draw from independent port spaces.
- `TIME_WAIT` from a previous run can exhaust the range even before this run starts — avoid back-to-back runs on the same host without a pause (plan decision D35's finding 8).

