// Phase 5 D30 client probe (JS, @microsoft/signalr 8.0.17). Implements exactly the scenario in
// tests/Keryhe.Switchboard.CompatibilityTests/ClientProbeContract.md — kept byte-for-byte parallel
// with tests/clients/js/v10/probe.mjs so a JS-version-specific defect (the kind finding 1 found for
// .NET 8) can't hide behind a scenario difference between the two.
//
// Usage: node probe.mjs <apiBaseUrl> <transport> <protocol>

import { HubConnectionBuilder, HttpTransportType } from "@microsoft/signalr";
import { MessagePackHubProtocol } from "@microsoft/signalr-protocol-msgpack";

const completedSteps = [];

function parseTransport(value) {
  switch (value.toLowerCase()) {
    case "websockets":
      return HttpTransportType.WebSockets;
    case "sse":
      return HttpTransportType.ServerSentEvents;
    case "longpolling":
      return HttpTransportType.LongPolling;
    default:
      throw new Error(`Unknown transport '${value}'.`);
  }
}

function parseProtocol(value) {
  switch (value.toLowerCase()) {
    case "json":
      return false;
    case "messagepack":
      return true;
    default:
      throw new Error(`Unknown protocol '${value}'.`);
  }
}

function nextStep(lastCompleted) {
  const order = ["connect", "receive_push", "join_group", "invoke", "receive_group", "disconnect"];
  const index = order.indexOf(lastCompleted);
  return index < 0 || index === order.length - 1 ? "unknown" : order[index + 1];
}

function withTimeout(promise, seconds, label) {
  return Promise.race([
    promise,
    new Promise((_, reject) => setTimeout(() => reject(new Error(`timeout:${label}`)), seconds * 1000)),
  ]);
}

async function main() {
  const [apiBaseUrl, transportArg, protocolArg] = process.argv.slice(2);
  if (!apiBaseUrl || !transportArg || !protocolArg) {
    console.log("RESULT FAIL step=args reason=expected-3-arguments");
    process.exitCode = 1;
    return;
  }

  const transport = parseTransport(transportArg);
  const useMessagePack = parseProtocol(protocolArg);
  const suffix = Math.random().toString(16).slice(2, 10);
  const roomId = `probe-room-${suffix}`;

  const loginResponse = await fetch(`${apiBaseUrl}/api/auth/login`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ username: `probe-${suffix}` }),
  });
  if (!loginResponse.ok) {
    throw new Error(`login-failed:${loginResponse.status}`);
  }

  const { accessToken } = await loginResponse.json();

  const builder = new HubConnectionBuilder().withUrl(`${apiBaseUrl}/chatHub`, {
    transport,
    accessTokenFactory: () => accessToken,
  });

  if (useMessagePack) {
    builder.withHubProtocol(new MessagePackHubProtocol());
  }

  const connection = builder.build();

  const connected = new Promise((resolve) => connection.on("Connected", (id) => resolve(id)));
  const groupMessage = new Promise((resolve) => connection.on("ReceiveMessage", (msg) => resolve(msg)));

  await withTimeout(connection.start(), 15, "connect");
  completedSteps.push("connect");

  await withTimeout(connected, 10, "receive_push");
  completedSteps.push("receive_push");

  await withTimeout(connection.invoke("JoinRoom", roomId), 10, "join_group");
  completedSteps.push("join_group");

  await withTimeout(connection.invoke("SendMessage", roomId, "probe-payload"), 10, "invoke");
  completedSteps.push("invoke");

  const message = await withTimeout(groupMessage, 10, "receive_group");
  // MessagePack args are serialized contractless (no camelCase policy applied, unlike the JSON
  // hub protocol), so ChatHub.SendMessage's anonymous { From, Text, SentAt } arrives as
  // PascalCase over MessagePack but camelCase over JSON — accept either.
  const text = message.text ?? message.Text;
  if (text !== "probe-payload") {
    console.log(`RESULT FAIL step=receive_group reason=unexpected-payload:${text}`);
    process.exitCode = 1;
    await connection.stop().catch(() => {});
    return;
  }

  completedSteps.push("receive_group");

  await withTimeout(connection.stop(), 10, "disconnect");
  completedSteps.push("disconnect");

  console.log(`RESULT OK steps=${completedSteps.join(",")}`);
}

main()
  .catch((err) => {
    const step = completedSteps.length === 0 ? "connect" : nextStep(completedSteps[completedSteps.length - 1]);
    console.error(err);
    console.log(`RESULT FAIL step=${step} reason=${err.constructor.name}:${err.message}`);
    process.exitCode = 1;
  })
  .finally(() => {
    // An open WebSocket/long-polling handle keeps the event loop alive even after the RESULT
    // line is printed — without this, ProbeRunner's process-exit wait times out even on a
    // correctly-diagnosed failure (verified: this happened for the MessagePack cells above).
    process.exit(process.exitCode ?? 0);
  });
