# `@microsoft/signalr` redirect check

Carried forward from the retired Phase 0 spike (`spike/Phase0.Spike.JsClient/`), per the Phase 1
plan's promotion map: *"the JS client belongs with the Angular deliverable; keep the script until
then."* It is the reference for driving an **unmodified** `@microsoft/signalr` client through the
two-step negotiate redirect — the thing the Angular sample (`SampleChatApp.Angular`) also has to
get right, verified here without any Angular/browser tooling in the loop.

Retargeted for Phase 2 (Slice 9): it now points at `SampleChatApp.Api`'s real `/chatHub` route
instead of the retired spike host, logs in via `SampleChatApp.Api`'s dev-only `/api/auth/login` to
get a real user JWT (`ChatHub` is `[Authorize]`), and — instead of the spike-only `__diag`
assertion, which the real `Keryhe.Switchboard.Server` deliberately doesn't expose — asserts that a
real hub method invocation (`JoinRoom` + `SendMessage`) round-trips back to the same client via
`ReceiveMessage`. Reaching `Connected` alone wouldn't catch a routing bug downstream of the
redirect; the round-trip does.

Three real processes need to be running:

```bash
dotnet run --project ../../../src/Keryhe.Switchboard.Server
dotnet run --project ../SampleChatApp.Api
npm install && node redirect-check.mjs http://localhost:5001
```

(adjust the `Switchboard:Url`/`Switchboard:ServerToken` config on `SampleChatApp.Api` — see its own
`appsettings.json` — to match wherever `Keryhe.Switchboard.Server` is actually listening.)

The original spike version is recoverable at `spike/Phase0.Spike.JsClient/redirect-check.mjs` in
git history (commit `b7456b2`, "Phase 0 - Complete").
