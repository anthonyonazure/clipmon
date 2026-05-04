// Clipmon relay server.
//
// What it does:
//   - Accepts WebSocket connections.
//   - Joins each connection to a "room" identified by an opaque hash sent by the client.
//   - Forwards messages to every other connection in the same room.
//
// What it does NOT do:
//   - It never sees the pairing code (only the SHA-256 hash of it).
//   - It never decrypts payloads. Each "clip" message carries an opaque AES-GCM envelope.
//   - It does not persist anything. Restarting the server clears all rooms.
//
// Wire protocol (JSON):
//   client -> server : { type:"join",  room:"<hex hash>", deviceId:"...", deviceName:"..." }
//   client -> server : { type:"clip",  room:"<hex hash>", envelope:"<base64 ciphertext>" }
//   server -> client : { type:"joined", peers:[{deviceId,deviceName}] }
//   server -> peers  : { type:"peer-joined", deviceId, deviceName }
//   server -> peers  : { type:"peer-left",   deviceId }
//   server -> peers  : { type:"clip", fromDeviceId, fromDeviceName, envelope, ts }

import http from "node:http";
import { WebSocketServer } from "ws";

const PORT = Number(process.env.PORT) || 8765;
const MAX_MESSAGE_BYTES = Number(process.env.MAX_MESSAGE_BYTES) || 4 * 1024 * 1024; // 4 MB
const HEARTBEAT_MS = 30_000;

/** @type {Map<string, Set<import("ws").WebSocket>>} */
const rooms = new Map();

function joinRoom(ws, room) {
  let set = rooms.get(room);
  if (!set) {
    set = new Set();
    rooms.set(room, set);
  }
  set.add(ws);
  ws._room = room;
}

function leaveRoom(ws) {
  const room = ws._room;
  if (!room) return;
  const set = rooms.get(room);
  if (!set) return;
  set.delete(ws);
  if (set.size === 0) rooms.delete(room);
}

function broadcastToRoom(room, payload, exceptWs) {
  const set = rooms.get(room);
  if (!set) return;
  const data = JSON.stringify(payload);
  for (const peer of set) {
    if (peer === exceptWs) continue;
    if (peer.readyState !== peer.OPEN) continue;
    peer.send(data);
  }
}

function safeSend(ws, payload) {
  if (ws.readyState !== ws.OPEN) return;
  ws.send(JSON.stringify(payload));
}

function handleMessage(ws, raw) {
  if (raw.byteLength > MAX_MESSAGE_BYTES) {
    safeSend(ws, { type: "error", code: "too-large" });
    return;
  }

  let msg;
  try {
    msg = JSON.parse(raw.toString("utf8"));
  } catch {
    safeSend(ws, { type: "error", code: "bad-json" });
    return;
  }

  if (!msg || typeof msg !== "object") {
    safeSend(ws, { type: "error", code: "bad-shape" });
    return;
  }

  switch (msg.type) {
    case "join": {
      if (typeof msg.room !== "string" || msg.room.length < 16 || msg.room.length > 256) {
        safeSend(ws, { type: "error", code: "bad-room" });
        return;
      }
      if (typeof msg.deviceId !== "string" || msg.deviceId.length === 0) {
        safeSend(ws, { type: "error", code: "bad-device-id" });
        return;
      }
      if (ws._room) leaveRoom(ws);

      ws._deviceId = String(msg.deviceId).slice(0, 128);
      ws._deviceName = String(msg.deviceName || "").slice(0, 128);

      // Snapshot peer list BEFORE adding the new one.
      const peers = [];
      const existing = rooms.get(msg.room);
      if (existing) {
        for (const peer of existing) {
          peers.push({ deviceId: peer._deviceId, deviceName: peer._deviceName });
        }
      }

      joinRoom(ws, msg.room);

      safeSend(ws, { type: "joined", peers });
      broadcastToRoom(msg.room, {
        type: "peer-joined",
        deviceId: ws._deviceId,
        deviceName: ws._deviceName,
      }, ws);
      return;
    }

    case "clip": {
      if (!ws._room) {
        safeSend(ws, { type: "error", code: "not-joined" });
        return;
      }
      if (typeof msg.envelope !== "string" || msg.envelope.length === 0) {
        safeSend(ws, { type: "error", code: "bad-envelope" });
        return;
      }
      const outbound = {
        type: "clip",
        fromDeviceId: ws._deviceId,
        fromDeviceName: ws._deviceName,
        envelope: msg.envelope,
        ts: Date.now(),
        backfill: !!msg.backfill,
      };

      // Targeted delivery (used by backfill): forward only to the requested peer.
      if (typeof msg.targetDeviceId === "string" && msg.targetDeviceId.length > 0) {
        const set = rooms.get(ws._room);
        if (set) {
          for (const peer of set) {
            if (peer === ws) continue;
            if (peer._deviceId === msg.targetDeviceId && peer.readyState === peer.OPEN) {
              peer.send(JSON.stringify(outbound));
            }
          }
        }
        return;
      }

      broadcastToRoom(ws._room, outbound, ws);
      return;
    }

    case "ping": {
      safeSend(ws, { type: "pong", ts: Date.now() });
      return;
    }

    default:
      safeSend(ws, { type: "error", code: "unknown-type" });
  }
}

const server = http.createServer((req, res) => {
  if (req.url === "/health") {
    res.writeHead(200, { "content-type": "application/json" });
    res.end(JSON.stringify({ ok: true, rooms: rooms.size, uptime: process.uptime() }));
    return;
  }
  res.writeHead(200, { "content-type": "text/plain" });
  res.end("Clipmon relay is running. WebSocket only.");
});

const wss = new WebSocketServer({ server, maxPayload: MAX_MESSAGE_BYTES });

wss.on("connection", (ws) => {
  ws.isAlive = true;
  ws.on("pong", () => { ws.isAlive = true; });

  ws.on("message", (raw) => handleMessage(ws, raw));

  ws.on("close", () => {
    if (ws._room) {
      const room = ws._room;
      leaveRoom(ws);
      broadcastToRoom(room, { type: "peer-left", deviceId: ws._deviceId });
    }
  });

  ws.on("error", () => {
    // Browsers and clients can dump errors during teardown; nothing to do.
  });
});

const heartbeat = setInterval(() => {
  for (const ws of wss.clients) {
    if (!ws.isAlive) {
      ws.terminate();
      continue;
    }
    ws.isAlive = false;
    try { ws.ping(); } catch { /* ignore */ }
  }
}, HEARTBEAT_MS);

wss.on("close", () => clearInterval(heartbeat));

server.listen(PORT, () => {
  console.log(`Clipmon relay listening on :${PORT}`);
});
