// Slice 9 (Phase 2): an unmodified @microsoft/signalr client negotiates against SampleChatApp.Api's
// MapHub-mapped /chatHub route, follows the two-step redirect through to Switchboard's WebSocket
// transport (no SignalR fork, no reflection into internals), then proves the connection actually
// works end to end by round-tripping a real hub method invocation — not just reaching the
// Connected state, which the negotiate redirect alone could produce even if routing were broken.
//
// Usage: node redirect-check.mjs [baseUrl]
// Expects both real hosts already running:
//   dotnet run --project ../../../src/Keryhe.Switchboard.Server
//   dotnet run --project ../SampleChatApp.Api
//   npm install && node redirect-check.mjs http://localhost:5001

import { HubConnectionBuilder } from "@microsoft/signalr";

const baseUrl = process.argv[2] ?? "http://localhost:5001";
const username = `redirect-check-${Date.now()}`;

async function login() {
  const response = await fetch(`${baseUrl}/api/auth/login`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ username }),
  });

  if (!response.ok) {
    throw new Error(`Login failed: ${response.status} ${await response.text()}`);
  }

  const { accessToken } = await response.json();
  return accessToken;
}

const accessToken = await login();

const connection = new HubConnectionBuilder()
  .withUrl(`${baseUrl}/chatHub`, {
    accessTokenFactory: () => accessToken,
  })
  .build();

const roomId = "redirect-check-room";
const messageReceived = new Promise((resolve, reject) => {
  connection.on("ReceiveMessage", (msg) => resolve(msg));
  setTimeout(() => reject(new Error("Timed out waiting for ReceiveMessage")), 10_000);
});

try {
  await connection.start();

  if (connection.state !== "Connected") {
    console.error(`FAIL: expected state Connected, got ${connection.state}`);
    process.exit(1);
  }

  console.log("PASS: @microsoft/signalr client reached Connected via the negotiate redirect.");

  await connection.invoke("JoinRoom", roomId);
  await connection.invoke("SendMessage", roomId, "redirect-check-ping");

  const msg = await messageReceived;
  if (msg.from !== username || msg.text !== "redirect-check-ping") {
    console.error("FAIL: round-tripped message did not match what was sent.");
    console.error(JSON.stringify(msg));
    process.exit(1);
  }

  console.log("PASS: hub method invocation round-tripped through Switchboard back to this client.");

  await connection.stop();
  process.exit(0);
} catch (err) {
  console.error("FAIL:", err);
  process.exit(1);
}
