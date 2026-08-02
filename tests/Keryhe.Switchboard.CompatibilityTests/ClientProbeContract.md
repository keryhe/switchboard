# Client probe contract (plan decision D30)

Every SDK's probe is a small standalone executable that runs the **same scenario** against a
real, already-running `SampleChatApp.Api` (which fronts a real, already-running
`Keryhe.Switchboard.Server`). It is what makes four language implementations comparable — a probe
that quietly skips a step turns a red cell green for the wrong reason (plan §5).

## Invocation

```
<probe> <apiBaseUrl> <transport> <protocol>
```

- `apiBaseUrl` — e.g. `http://127.0.0.1:54231`, the `SampleChatApp.Api` base address.
- `transport` — one of `websockets`, `sse`, `longpolling` (case-insensitive).
- `protocol` — one of `json`, `messagepack` (case-insensitive).

## Scenario (every probe implements exactly this, in this order)

1. **Log in** — `POST {apiBaseUrl}/api/auth/login` with `{"username":"probe-<random>"}`, read
   `accessToken` from the JSON response.
2. **Connect** — build a `HubConnection` against `{apiBaseUrl}/chatHub`, pinned to the requested
   transport and hub protocol, with the access token as a bearer token.
3. **Receive caller push** — wait for the server-initiated `Connected` invocation
   (`ChatHub.OnConnectedAsync`'s `Clients.Caller.SendAsync("Connected", ...)`).
4. **Join group** — invoke `JoinRoom("probe-room-<random>")` and wait for it to complete.
5. **Invoke hub method** — invoke `SendMessage("probe-room-<random>", "probe-payload")` and wait
   for it to complete.
6. **Receive group message** — wait for the `ReceiveMessage` invocation the group send in step 5
   produces, and verify its `text` field equals `"probe-payload"`.
7. **Clean disconnect** — `StopAsync()`.

Each step has its own bounded wait (15s connect, 10s everything else) — a probe must never hang
indefinitely; a timeout is a `RESULT FAIL` with a `timeout:<step>` reason, not a hang.

## Output contract

Exactly one line on stdout, always the last line written before the process exits:

```
RESULT OK steps=connect,receive_push,join_group,invoke,receive_group,disconnect
RESULT FAIL step=<step-name> reason=<short-reason>
```

`steps=` lists every step that actually completed, comma-separated, in order — this is what lets
`ProbeRunner`/`CompatibilityMatrixTests` catch a probe that silently skips a step: a `RESULT OK`
whose `steps=` list is short is treated as a failure, not a pass.

Exit code is `0` for `RESULT OK`, non-zero for `RESULT FAIL` or an unhandled exception (in which
case the exception detail goes to stderr and the last stdout line is still a `RESULT FAIL` printed
from a top-level catch).
