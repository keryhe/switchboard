# Operations Guide

This is the Phase 4 deliverable [ADR-004](07-adr/ADR-004-token-authority.md) originally described as forthcoming: how to generate, rotate, and store the three token types this service uses, how to reach the management API safely, and what each exported metric means when you're looking at a dashboard at 2am.

---

## 1. The three token types

Switchboard never reuses a signing key across these three roles. That is deliberate, not incidental — an app server token must never be able to drive the management API, and vice versa. See [ADR-004](07-adr/ADR-004-token-authority.md) for the full rationale.

| Token | Signing key | Audience | Typical lifetime | Who holds it |
|---|---|---|---|---|
| Client | `TokenSigningKey` | `switchboard-client` | ~60s | Minted by Switchboard itself at negotiate time; the client never generates one |
| App server | `ServerSigningKey` | `switchboard-server` | ~24h | Generated once per app server via the CLI, configured into that app server's `AddSwitchboardConnector()` setup |
| Management | `ManagementSigningKey` | `switchboard-management` | ~24h | Generated per operator/tool, presented as a bearer token to `/api/v1/*` |

Each of the two long-lived keys (`ServerSigningKey`, `ManagementSigningKey`) has an independent `…Fallback` key used only during rotation (§3). `TokenSigningKey` has no fallback — client tokens live for ~60 seconds, so a rotation window for them is not a real operational concern.

A management token presented against `/server/{hub}` (the app-server WebSocket endpoint) or a server token presented against `/api/v1/*` both fail — not with a role-specific error, but with the same `401 Unauthorized` a garbage token would get. Each token is validated only against its own audience and signing key; a wrong-type token fails that check before "role" is ever inspected, which is why it's cryptographically indistinguishable from noise rather than a recognizable-but-wrong credential.

---

## 2. Generating tokens

The service host doubles as a CLI when invoked with a `token` subcommand — no separate tool to install:

```bash
# App server token — one per app server, configured into that app server's own startup code.
dotnet run --project src/Keryhe.Switchboard.Server --no-build -- token generate \
  --role appserver \
  --server-id chat-api-1 \
  --hubs chatHub \
  --ttl 24h \
  --key <ServerSigningKey>
```

```bash
# Management token — one per operator or dashboard tool.
dotnet run --project src/Keryhe.Switchboard.Server --no-build -- token generate \
  --role management \
  --subject ops-dashboard \
  --ttl 24h \
  --key <ManagementSigningKey>
```

`--hubs` is comma-separated for an app server that serves more than one hub. `--ttl` accepts a plain duration (`24h`, `30m`, `60s`). The signing key passed via `--key` must match whatever `ServerSigningKey`/`ManagementSigningKey` the running service was configured with — the CLI signs the token locally; it does not call the running service.

---

## 3. Rotating a signing key

Both long-lived keys support the same two-key rotation procedure, using the `…Fallback` slot:

1. **Stage the new key.** Set the currently-active key as the `…Fallback` value, and put a newly generated key in the primary slot. Restart (or roll) the service with both configured. `ITokenService.Validate` accepts tokens signed by either key during this window — nothing holding an old token is invalidated yet.
2. **Reissue tokens against the new primary key.** Generate new app server / management tokens signed with the new key (§2) and roll them out to the app servers / tools that need them, at whatever pace your deployment process allows.
3. **Retire the old key.** Once you're confident nothing is still presenting a token signed with the old key (its TTL has fully elapsed, or you've confirmed every holder has the new one), remove it from the `…Fallback` slot entirely. Leaving a retired key in `…Fallback` indefinitely defeats the point of rotating it.

There is no online "list who's using which key" — token issuance in this service is deliberately stateless (a signed JWT, not a server-side session), so rotation planning is a matter of TTL and rollout discipline, not a revocation call.

---

## 4. Secret storage

`TokenSigningKey`, `ServerSigningKey`, `ManagementSigningKey`, and both `…Fallback` slots are plain configuration values (`SwitchboardOptions`), read the same way as any other ASP.NET Core configuration — environment variables, a mounted secrets file, or a secret manager integrated via a custom `IConfigurationSource`. This service does not implement its own secret store or KMS integration; treat these exactly as you would any other application secret in your environment (never in source control, never in a plain-text config file checked into a repo, rotated on the same cadence as other production credentials).

`OrleansAdoNetConnectionString` and the CLI's own `--key` argument are the same category of secret and deserve the same handling — a connection string or signing key typed on a shell command line lands in shell history and process listings; prefer piping it from a secret manager over hardcoding it in a script.

---

## 5. Reaching the management API safely

The management API is mapped onto the **same** Kestrel listener as ordinary client traffic (`/api/v1/*`, alongside `/{hub}`, `/negotiate`, `/server/{hub}`) — there is no separate port to firewall off. Two independent controls apply:

1. **`EnableManagementApi` + `ManagementSigningKey`.** With `EnableManagementApi` left at its default (`false`), or set `true` with no `ManagementSigningKey` configured, `/api/v1/*` is **not mapped at all** — a `404`, not an unauthenticated `401` surface. Turning the feature on with no key configured fails the service's own startup validation.
2. **`ManagementAllowedNetworks`.** A CIDR allowlist (e.g. `["10.0.0.0/8"]`) evaluated against the caller's IP before the bearer token is even checked. Empty (the default) means no network restriction beyond the token itself. **Recommended for any production deployment**: scope this to your internal/ops network range, so a leaked or long-lived management token isn't a public-internet-reachable admin surface on its own.

Both controls are independent of each other and of the bearer token check — all three must pass for a request to succeed.

---

## 6. Metrics reference

All metrics are exported via OTLP only (no Prometheus scrape endpoint) when `OtlpEndpoint` is configured. **A misconfigured OTLP endpoint fails completely silently** — no exception, no log line, nothing on stdout — so the service logs the configured endpoint once at startup (`OpenTelemetry metrics export configured: OtlpEndpoint=...`) specifically so a typo doesn't look identical to a working exporter. If you don't see that log line, no OTLP pipeline was constructed at all.

| Metric | Kind | Tags | What it tells you |
|---|---|---|---|
| `signalr.client_connections.active` | Gauge | `hub` | This **node's own** live client count, by hub. Node-local by design — sum across nodes in your dashboard/query layer for a cluster total; a single node's value dropping to zero while others hold steady usually means that node restarted or lost its clients, not that the hub is empty. |
| `signalr.server_connections.active` | Gauge | `hub` | This node's own app-server WebSocket count, by hub. Same node-local caveat as above. A hub with client connections but zero server connections on every node is the D18 "assignment" gate — negotiate for that hub will start failing with 503. |
| `signalr.messages.routed` | Counter | `direction` (`inbound`/`outbound`), `hub` | Throughput. `inbound` = client→app-server; `outbound` = app-server→client(s). A sudden drop in `outbound` with steady `inbound` suggests app servers have stopped sending, not a client-side problem. |
| `signalr.broadcast.fan_out_size` | Histogram | — | Recipient count of a single broadcast/group/user send, local to the issuing node. Watch this alongside `messages.routed{direction=outbound}` — a broadcast with a fan-out size much smaller than expected active connections usually means a protocol-mismatch drop (see `envelopes.unrouted{reason=no_payload_for_protocol}` below), not that clients silently vanished. |
| `signalr.message.inbound_duration` / `signalr.message.outbound_duration` | Histogram (ms) | `cross_node` (inbound only) | How long **this proxy** takes to route a message — not client→server round-trip latency, which this service cannot observe without parsing payloads it deliberately never parses (see [04-design.md §13](04-design.md#13-observability-phase-4)). A rising `cross_node=true` inbound duration relative to `cross_node=false` points at backplane/cluster overhead, not application logic. |
| `signalr.envelopes.unrouted` | Counter | `reason` | Messages the service could not deliver, by cause: `unknown_connection` (routed to an id that was never registered — usually a stale reference in app-server code), `malformed_server_connection_ref` (an internal-format bug, should never happen in practice), `server_connection_gone` (the assigned app-server WebSocket disappeared between assignment and use — a reconnect storm symptom), `no_payload_for_protocol` (a fan-out target's negotiated protocol had no matching encoded payload — a management-send-argument-encoding bug, or an app server sending only one protocol's `Payloads` entry), `no_node_subscribed` (clustered mode only — a cross-node message had nowhere to go because no node had subscribed an observer for that hub; this is the exact Phase 3 Slice 7 failure mode and is worth alerting on directly). |
| `signalr.pending_connections.created` / `.consumed` / `.expired` | Counters | — | The negotiate → transport-upgrade handoff. In-flight count is `created - consumed - expired`, computed in your dashboard. A growing gap between `created` and `consumed + expired` over a short window means clients are negotiating but never completing the transport upgrade — check client-side network reachability to the negotiated `url`. |

**Tracing.** Negotiate and client-connect spans (`negotiate`, `client_connect`) are always exported when `OtlpEndpoint` is configured — one per connection, tagged `hub`/`connectionId`/`node.id`. A `message_route` span exists but is off by default (`TraceMessageRouting = false`); turning it on puts a span on every single routed message, which at broadcast fan-out rates is real overhead and real trace-storage volume — enable it only for short, targeted debugging sessions, not as a standing production setting.

**Logs.** Structured (`ILogger`) output — connection lifecycle, routing errors, server-connection health — exports through the same OTLP pipeline as metrics and traces when `OtlpEndpoint` is configured, so all three signals land in one backend together rather than needing separate log shipping.

---

## 7. Health endpoints — which one to use where

Two endpoints exist, deliberately kept separate:

- **`GET /healthz`** — public, unauthenticated, answered from a cached in-process value (never inline cluster I/O). This is what your load balancer / orchestrator should probe. Body is intentionally minimal: `{"status":"healthy"}` or a `503` with `{"status":"unhealthy"}` — no counts, no hub names.
- **`GET /api/v1/health`** — authenticated (management token), may do real cluster-wide reads, returns per-hub server-connection counts and a total client-connection count. Use this for dashboards and human-driven investigation, called at human rates — never wire a load balancer's probe cadence to this endpoint, since that would turn every probe interval into cluster I/O across every node.
