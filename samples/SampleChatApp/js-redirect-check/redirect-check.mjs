// A5: an unmodified @microsoft/signalr client negotiates against the MapHub-mapped /testHub
// route, follows the redirect purely via SwitchboardNegotiateMatcherPolicy, and reaches the
// stub target's WebSocket. No SignalR fork, no reflection into internals.
//
// Usage: node redirect-check.mjs [baseUrl]
// Expects the host already running (see spike plan §3/A5), e.g.:
//   dotnet run --project ../Phase0.Spike.Host --urls http://localhost:5559

import { HubConnectionBuilder, HttpTransportType } from "@microsoft/signalr";

const baseUrl = process.argv[2] ?? "http://localhost:5559";

const connection = new HubConnectionBuilder()
  .withUrl(`${baseUrl}/testHub`, {
    transport: HttpTransportType.WebSockets,
  })
  .build();

try {
  await connection.start();

  if (connection.state !== "Connected") {
    console.error(`FAIL: expected state Connected, got ${connection.state}`);
    process.exit(1);
  }

  console.log("PASS: @microsoft/signalr client reached Connected via the negotiate redirect.");

  const observedResponse = await fetch(`${baseUrl}/__diag/stub-observed`);
  const observed = await observedResponse.json();

  if (!observed.connectedHubs.includes("testHub") || !observed.negotiatedHubs.includes("testHub")) {
    console.error("FAIL: stub target did not observe the expected negotiate/connect for testHub");
    console.error(JSON.stringify(observed));
    process.exit(1);
  }

  console.log("PASS: stub target observed both the step-2 negotiate and the socket upgrade.");

  await connection.stop();
  process.exit(0);
} catch (err) {
  console.error("FAIL:", err);
  process.exit(1);
}
