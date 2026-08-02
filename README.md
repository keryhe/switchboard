# Switchboard

A self-hosted connection proxy and scale-out backplane for ASP.NET Core SignalR, written in .NET 10 C#. It's an on-premise equivalent of Azure SignalR Service: clients connect to Switchboard instead of your app servers, and Switchboard fans messages back out across however many app servers you run — no Redis, no sticky sessions, no cloud dependency.

Existing SignalR clients (.NET, JavaScript/TypeScript, Java) connect exactly as they always have — full wire compatibility is a hard requirement. On the app server side, you swap `AddAzureSignalR()` for `AddSwitchboardConnector()`; your hub code doesn't change.

## Why

Plain ASP.NET Core SignalR has two problems at scale:

1. **Connection overhead** — every connected client holds a socket, thread-pool work, and memory on the app server, so app servers have to be sized for connection count, not application logic.
2. **Broken broadcasts under load balancing** — a broadcast from server A never reaches clients on server B unless you bolt on a backplane (typically Redis) and usually still need sticky sessions.

Switchboard solves both: clients connect to Switchboard, and app servers hold a small fixed pool of connections to it (default 5 per hub) regardless of client count. Because every client connects through Switchboard, it has a complete view of all connections and can fan out broadcasts/group/user messages across every app server with no sticky sessions.

See [docs/docs/01-overview.md](docs/docs/01-overview.md) for the full problem statement and a feature comparison against a Redis backplane and Azure SignalR Service.

## Status

All five planned phases are complete — connection proxying, full transport/protocol support (WebSocket, SSE, Long Polling; JSON and MessagePack), Orleans-backed scale-out clustering, a management REST API with metrics/tracing, and a real-client compatibility matrix + load testing. See [CLAUDE.md](CLAUDE.md) for the detailed phase-by-phase status and [docs/docs/00-review-findings.md](docs/docs/00-review-findings.md) for the full results log.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Node.js + npm (only if you want to run the Angular sample or the JS compatibility probes)
- Docker (only for the Orleans/ADO.NET clustering tests, the OTLP-collector-backed tests, and the Java compatibility probe — everything else runs without it)

## Quickstart

Clone and build the whole solution:

```bash
git clone <this-repo>
cd switchboard
dotnet build Switchboard.sln
```

### Run the service

```bash
dotnet run --project src/Keryhe.Switchboard.Server
```

This starts Switchboard on `http://localhost:5000` using the dev-only signing keys baked into `appsettings.json` — fine for trying it out locally, **do not use them in production** (see [docs/docs/10-operations.md](docs/docs/10-operations.md) for real secret management and key rotation).

### Add it to your app server

```bash
dotnet add package Keryhe.Switchboard.Connector
```

```csharp
builder.Services.AddSignalR();
builder.Services.AddSwitchboardConnector(options =>
{
    options.ServiceUrl = "http://localhost:5000";
    options.ServerAccessToken = "<a server token — see below>";
});
```

Generate a server token with the built-in CLI:

```bash
dotnet run --project src/Keryhe.Switchboard.Server --no-build -- token generate \
  --role appserver --server-id my-api --hubs chatHub --ttl 24h \
  --key dev-only-server-signing-key-change-me-32+
```

That's the whole integration — no hub code changes required.

### Try the sample chat app

`samples/SampleChatApp` is a working end-to-end reference: an ASP.NET Core API using the Connector, plus an Angular client.

```bash
# Terminal 1 — the proxy (as above)
dotnet run --project src/Keryhe.Switchboard.Server

# Terminal 2 — the sample API (update its ServerToken in appsettings.json first, generated as above)
dotnet run --project samples/SampleChatApp/SampleChatApp.Api

# Terminal 3 — the Angular UI
cd samples/SampleChatApp/SampleChatApp.Angular
npm install
npm start
```

Full walkthrough, including the connection flow and what each piece demonstrates: [docs/docs/08-sample-app.md](docs/docs/08-sample-app.md).

## Running the tests

```bash
dotnet test tests/Keryhe.Switchboard.UnitTests/Keryhe.Switchboard.UnitTests.csproj
dotnet test tests/Keryhe.Switchboard.IntegrationTests/Keryhe.Switchboard.IntegrationTests.csproj
dotnet test tests/Keryhe.Switchboard.CompatibilityTests/Keryhe.Switchboard.CompatibilityTests.csproj   # real SDK x transport x protocol matrix
```

Benchmarks and load testing:

```bash
dotnet run -c Release --project tests/Keryhe.Switchboard.Benchmarks
dotnet run -c Release --project tests/Keryhe.Switchboard.LoadHarness -- --target-clients 10000
```

Observed results from real runs: [docs/docs/11-compatibility-matrix.md](docs/docs/11-compatibility-matrix.md), [docs/docs/12-performance.md](docs/docs/12-performance.md).

## Documentation

Start at [docs/README.md](docs/README.md) for the full documentation index (architecture, wire protocol, data models, ADRs, operations guide). [CLAUDE.md](CLAUDE.md) has the current project status and a map of the solution layout.

## License

[MIT](LICENSE)
