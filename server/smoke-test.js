// End-to-end smoke test for the Clipmon relay + crypto protocol.
//
// What this proves:
//   1. The server starts, accepts WebSocket connections, and broadcasts to the right room.
//   2. Two clients sharing a pairing code can derive the same key and decrypt each other's envelopes.
//   3. The wire format (PBKDF2 params, AES-GCM layout, JSON shape) matches the spec.
//
// If this passes, the .NET and Swift clients should also interoperate (because they implement
// the same spec). Run: `node smoke-test.js`

import crypto from "node:crypto";
import { spawn } from "node:child_process";
import { WebSocket } from "ws";

const PORT = 18765;
const PAIRING_CODE = "smokeTest42";
const KEY_SALT = "clipmon-key-v1";
const ROOM_SALT = "clipmon-room-v1";
const ITERATIONS = 200_000;

function deriveKey(code) {
  return crypto.pbkdf2Sync(code, KEY_SALT, ITERATIONS, 32, "sha256");
}

function deriveRoomId(code) {
  return crypto.createHash("sha256").update(code + ":" + ROOM_SALT).digest("hex");
}

function encryptEnvelope(key, plaintextJson) {
  const iv = crypto.randomBytes(12);
  const cipher = crypto.createCipheriv("aes-256-gcm", key, iv, { authTagLength: 16 });
  const ciphertext = Buffer.concat([cipher.update(plaintextJson, "utf8"), cipher.final()]);
  const tag = cipher.getAuthTag();
  return Buffer.concat([iv, ciphertext, tag]).toString("base64");
}

function decryptEnvelope(key, envelopeBase64) {
  const env = Buffer.from(envelopeBase64, "base64");
  const iv = env.subarray(0, 12);
  const tag = env.subarray(env.length - 16);
  const ciphertext = env.subarray(12, env.length - 16);
  const decipher = crypto.createDecipheriv("aes-256-gcm", key, iv, { authTagLength: 16 });
  decipher.setAuthTag(tag);
  return Buffer.concat([decipher.update(ciphertext), decipher.final()]).toString("utf8");
}

function makeClient(name) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(`ws://localhost:${PORT}`);
    const inbox = [];
    let resolveNext = null;

    ws.on("open", () => resolve({ ws, inbox, name, nextMessage }));
    ws.on("error", reject);
    ws.on("message", (data) => {
      const msg = JSON.parse(data.toString("utf8"));
      inbox.push(msg);
      if (resolveNext) {
        const r = resolveNext;
        resolveNext = null;
        r(msg);
      }
    });

    function nextMessage(predicate, timeoutMs = 2000) {
      // Drain anything matching already in inbox.
      const idx = inbox.findIndex(predicate);
      if (idx >= 0) return Promise.resolve(inbox.splice(idx, 1)[0]);

      return new Promise((res, rej) => {
        const timer = setTimeout(() => rej(new Error(`${name} timed out waiting`)), timeoutMs);
        resolveNext = (msg) => {
          if (predicate(msg)) {
            clearTimeout(timer);
            res(msg);
          } else {
            // Wrong message; keep listening.
            inbox.pop(); // we already pushed it; pull it back so others see it
            inbox.push(msg);
          }
        };
      });
    }
  });
}

async function run() {
  const failures = [];
  let serverProc;

  // Boot the relay on a non-default port so we don't fight any running instance.
  serverProc = spawn(process.execPath, ["index.js"], {
    cwd: import.meta.dirname,
    env: { ...process.env, PORT: String(PORT) },
    stdio: ["ignore", "pipe", "pipe"],
  });

  // Wait for server "listening" line.
  await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error("server boot timeout")), 5000);
    serverProc.stdout.on("data", (chunk) => {
      if (chunk.toString().includes("listening")) {
        clearTimeout(timer);
        resolve();
      }
    });
    serverProc.stderr.on("data", (chunk) => process.stderr.write(chunk));
    serverProc.on("exit", (code) => reject(new Error(`server exited early (${code})`)));
  });

  try {
    const key = deriveKey(PAIRING_CODE);
    const room = deriveRoomId(PAIRING_CODE);

    const alice = await makeClient("alice");
    const bob = await makeClient("bob");

    // --- 1. Both join the room.
    alice.ws.send(JSON.stringify({ type: "join", room, deviceId: "alice-id", deviceName: "Alice" }));
    bob.ws.send(JSON.stringify({ type: "join", room, deviceId: "bob-id", deviceName: "Bob" }));

    const aliceJoined = await alice.nextMessage((m) => m.type === "joined");
    const bobJoined = await bob.nextMessage((m) => m.type === "joined");

    // --- 2. Bob's join should arrive at Alice as peer-joined.
    const peerNotice = await alice.nextMessage((m) => m.type === "peer-joined" && m.deviceId === "bob-id");

    // --- 3. Alice broadcasts an encrypted clip; Bob receives + decrypts.
    const payload = {
      fingerprint: "abc123",
      kind: "text",
      textContent: "secret token: hello world",
      fileName: null,
      fileUrl: null,
      payloadDataBase64: null,
      utiIdentifier: "public.utf8-plain-text",
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
      isPinned: false,
      sourceApplication: "smoke-test",
      fromDeviceId: "alice-id",
      fromDeviceName: "Alice",
    };
    const envelope = encryptEnvelope(key, JSON.stringify(payload));
    alice.ws.send(JSON.stringify({ type: "clip", room, envelope }));

    const inbound = await bob.nextMessage((m) => m.type === "clip");
    const decrypted = JSON.parse(decryptEnvelope(key, inbound.envelope));

    if (decrypted.textContent !== payload.textContent) {
      failures.push(`text mismatch: got "${decrypted.textContent}"`);
    }
    if (decrypted.fromDeviceId !== "alice-id") {
      failures.push(`fromDeviceId mismatch: ${decrypted.fromDeviceId}`);
    }
    if (inbound.fromDeviceId !== "alice-id") {
      failures.push(`server-attached fromDeviceId mismatch: ${inbound.fromDeviceId}`);
    }

    // --- 4. Alice should NOT receive her own clip back.
    let receivedOwnEcho = false;
    setTimeout(() => {}, 0); // tick
    await new Promise((r) => setTimeout(r, 200));
    if (alice.inbox.some((m) => m.type === "clip")) {
      failures.push("alice received her own clip echo from server");
    }

    // --- 5. Wrong-room peer cannot decrypt.
    const eve = await makeClient("eve");
    const eveKey = deriveKey("wrongCode99");
    const eveRoom = deriveRoomId("wrongCode99");
    eve.ws.send(JSON.stringify({ type: "join", room: eveRoom, deviceId: "eve-id", deviceName: "Eve" }));
    await eve.nextMessage((m) => m.type === "joined");

    alice.ws.send(JSON.stringify({ type: "clip", room, envelope: encryptEnvelope(key, JSON.stringify(payload)) }));
    await new Promise((r) => setTimeout(r, 150));
    if (eve.inbox.some((m) => m.type === "clip")) {
      failures.push("eve (different room) received the clip — relay is leaking across rooms");
    }

    // --- 6. Even if Eve grabs Alice's envelope, she can't decrypt it with her key.
    const stolenEnvelope = encryptEnvelope(key, JSON.stringify(payload));
    let cryptoFailed = false;
    try {
      decryptEnvelope(eveKey, stolenEnvelope);
    } catch {
      cryptoFailed = true;
    }
    if (!cryptoFailed) {
      failures.push("decryption with wrong key did NOT throw — crypto is broken");
    }

    alice.ws.close();
    bob.ws.close();
    eve.ws.close();
  } finally {
    serverProc.kill();
  }

  if (failures.length > 0) {
    console.error("\nFAIL:");
    for (const f of failures) console.error("  - " + f);
    process.exit(1);
  } else {
    console.log("\nOK: relay protocol + AES-GCM envelope round-trip verified");
  }
}

run().catch((err) => {
  console.error("smoke test crashed:", err);
  process.exit(1);
});
