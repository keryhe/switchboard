# `@microsoft/signalr` redirect check

Carried forward from the retired Phase 0 spike (`spike/Phase0.Spike.JsClient/`), per the Phase 1
plan's promotion map: *"the JS client belongs with the Angular deliverable; keep the script until
then."* It is the reference for driving an **unmodified** `@microsoft/signalr` client through the
two-step negotiate redirect — the thing Phase 2's Angular work has to get right.

## It does not run as-is — retarget it first

`redirect-check.mjs` still points at the Phase 0 spike host, which no longer exists:

- it connects to `/testHub` on `http://localhost:5559` (the spike's throwaway host), and
- it asserts against `GET /__diag/stub-observed`, a spike-only diagnostic endpoint that the real
  `Keryhe.Switchboard.Server` deliberately does not expose.

For Phase 2, point it at `SampleChatApp.Api`'s `/chatHub` and replace the `__diag` assertion with a
real check (e.g. that a hub method invocation round-trips). Both processes need to be running:

```bash
dotnet run --project src/Keryhe.Switchboard.Server
dotnet run --project samples/SampleChatApp/SampleChatApp.Api
npm install && node redirect-check.mjs http://localhost:5001
```

The original spike version is recoverable at `spike/Phase0.Spike.JsClient/redirect-check.mjs` in
git history (commit `b7456b2`, "Phase 0 - Complete").
