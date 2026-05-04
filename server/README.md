# Clipmon relay

A tiny stateless WebSocket relay that forwards opaque AES-GCM envelopes between
Clipmon devices in the same room. The relay never sees the pairing code (only a
SHA-256 hash of it) and never decrypts payloads.

## Run locally

```bash
cd server
npm install
npm start    # listens on :8765
```

Point Clipmon at `ws://localhost:8765` from the same machine, or
`ws://<your-lan-ip>:8765` from another device on the same network.

## Deploy to Fly.io

```bash
fly launch --no-deploy --name clipmon-relay --region iad --copy-config
fly deploy
```

`fly.toml` is auto-generated; no extra config required since the server reads
`PORT` from the environment.

## Deploy to Railway / Render / any container host

The included `Dockerfile` works as-is. The container exposes port `8765` and
listens on `$PORT` if set.

## Health check

`GET /health` returns `{ ok: true, rooms: N, uptime: S }`.

## Wire protocol

See top of `index.js`. In short:

- Every message is JSON.
- Clients identify themselves with `{ type: "join", room: "<sha256 hex>", deviceId, deviceName }`.
- Clients broadcast clips with `{ type: "clip", room, envelope: "<base64>" }`.
- The server forwards `clip` messages to every other client in the same room and
  emits `peer-joined` / `peer-left` lifecycle events.

The envelope contents are encrypted by the client with AES-GCM using a key
derived from the pairing code (PBKDF2-HMAC-SHA256, 200 000 iterations). The
server has no way to decrypt them.
