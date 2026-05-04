// Long-running fake peer for sync debugging.
//
// Usage:
//   node peer.js <pairingCode> [--relay ws://host:8765]
//
// Joins the same room as Clipmon clients, decrypts every envelope it receives,
// and prints a one-line summary. Stays connected until you Ctrl+C.

import crypto from "node:crypto";
import readline from "node:readline";
import { WebSocket } from "ws";

const args = process.argv.slice(2);
const pairing = args[0];
if (!pairing) {
  console.error("usage: node peer.js <pairingCode> [--relay ws://host:port]");
  process.exit(2);
}
const relayIdx = args.indexOf("--relay");
const relay = relayIdx >= 0 ? args[relayIdx + 1] : "ws://localhost:8765";

const KEY_SALT = "clipmon-key-v1";
const ROOM_SALT = "clipmon-room-v1";
const ITERATIONS = 200_000;

const key = crypto.pbkdf2Sync(pairing, KEY_SALT, ITERATIONS, 32, "sha256");
const room = crypto.createHash("sha256").update(pairing + ":" + ROOM_SALT).digest("hex");

const deviceId = "peer-cli-" + crypto.randomBytes(4).toString("hex");
const deviceName = process.env.PEER_NAME || "node peer";

console.log(`Connecting to ${relay} as "${deviceName}"`);
console.log(`Pairing code: ${pairing}`);
console.log(`Room hash:    ${room.slice(0, 16)}…`);
console.log("");

const ws = new WebSocket(relay);

ws.on("open", () => {
  ws.send(JSON.stringify({ type: "join", room, deviceId, deviceName }));
});

ws.on("message", (raw) => {
  let msg;
  try { msg = JSON.parse(raw.toString("utf8")); } catch { return; }

  switch (msg.type) {
    case "joined": {
      const peers = (msg.peers || []).map((p) => p.deviceName).join(", ") || "(none yet)";
      console.log(`[joined]   peers in room: ${peers}`);
      break;
    }
    case "peer-joined":
      console.log(`[peer +]   ${msg.deviceName}`);
      break;
    case "peer-left":
      console.log(`[peer -]   ${msg.deviceId}`);
      break;
    case "clip": {
      const payload = decryptOrNull(msg.envelope);
      if (!payload) {
        console.log(`[clip]     (could not decrypt — wrong pairing code?)`);
        return;
      }
      const tag = msg.backfill ? "backfill" : "live";
      const kind = payload.kind;
      const preview = (payload.textContent || payload.fileName || "<no preview>")
        .replace(/\s+/g, " ")
        .slice(0, 100);
      const bytes = payload.payloadDataBase64
        ? `, ${Math.round(Buffer.from(payload.payloadDataBase64, "base64").length / 1024)} KB`
        : "";
      console.log(`[clip ${tag}]  from ${msg.fromDeviceName} · ${kind}${bytes}: ${preview}`);
      break;
    }
    case "error":
      console.log(`[error]    ${msg.code}`);
      break;
  }
});

ws.on("close", () => {
  console.log("[disconnected]");
  process.exit(0);
});

ws.on("error", (err) => {
  console.error("ws error:", err.message);
  process.exit(1);
});

function decryptOrNull(envelopeBase64) {
  try {
    const env = Buffer.from(envelopeBase64, "base64");
    if (env.length < 12 + 16) return null;
    const iv = env.subarray(0, 12);
    const tag = env.subarray(env.length - 16);
    const ciphertext = env.subarray(12, env.length - 16);
    const decipher = crypto.createDecipheriv("aes-256-gcm", key, iv, { authTagLength: 16 });
    decipher.setAuthTag(tag);
    const json = Buffer.concat([decipher.update(ciphertext), decipher.final()]).toString("utf8");
    return JSON.parse(json);
  } catch {
    return null;
  }
}

readline.emitKeypressEvents(process.stdin);
if (process.stdin.isTTY) process.stdin.setRawMode(true);
process.stdin.on("keypress", (_, key) => {
  if (key && key.ctrl && key.name === "c") {
    ws.close();
    process.exit(0);
  }
});
