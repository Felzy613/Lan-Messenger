/**
 * LAN Messenger Cloud Relay — Cloudflare Worker
 *
 * Provides a zero-account cloud mailbox for offline message delivery.
 * Messages are stored encrypted; the Worker never sees plaintext.
 *
 * Authentication (zero-knowledge):
 *   - Each device derives  relay_id  = SHA256(private_key_bytes || "relay-v1")  [private]
 *   - Devices publish      relay_id_hash = SHA256(relay_id)                     [in discovery]
 *   - To store: sender POSTs with target's relay_id_hash (learned from discovery)
 *   - To retrieve: recipient GETs with their own relay_id; Worker verifies
 *     SHA256(relay_id) == relay_id_hash embedded in stored messages.
 *
 * KV schema:
 *   msg:{message_id}   → StoredMessage JSON  (72-hour TTL)
 *   inbox:{relay_id_hash} → JSON array of message_ids  (72-hour TTL)
 *
 * Endpoints:
 *   POST   /store               Store an encrypted message
 *   GET    /pending?relay_id=…  Fetch all pending messages for this device
 *   DELETE /message/:id?relay_id=… Delete a delivered message
 */

interface Env {
  RELAY_STORE: KVNamespace;
}

interface StoredMessage {
  relay_id_hash: string;         // SHA256(relay_id) hex — the recipient's mailbox address
  message_id: string;            // 32-char lowercase hex
  ciphertext_b64: string;        // AES-GCM ciphertext already encrypted by sender
  nonce_b64: string;             // 12-byte nonce, base64
  sender_username: string;
  sender_public_key_b64: string; // sender's X25519 public key, base64
  timestamp: number;             // original send timestamp (Unix seconds)
  stored_at: number;             // when we stored it (Unix seconds)
}

const TTL_SECONDS = 72 * 60 * 60;   // 72 hours
const MAX_INBOX = 100;               // max messages per recipient
// Base64-encoded ciphertext length limit. AES-GCM overhead is 16-byte tag → base64 of
// (plaintext + 16 bytes). A 4 KB text message base64-encodes to ~5.5 KB. Allow 16 KB
// to accommodate rich text with embedded data; well within KV value limits (25 MB).
const MAX_CT_LEN = 16384;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

async function sha256Hex(bytes: Uint8Array): Promise<string> {
  const buf = await crypto.subtle.digest("SHA-256", bytes);
  return Array.from(new Uint8Array(buf))
    .map((b) => b.toString(16).padStart(2, "0"))
    .join("");
}

function fromHex(hex: string): Uint8Array {
  const out = new Uint8Array(hex.length / 2);
  for (let i = 0; i < hex.length; i += 2) {
    out[i >> 1] = parseInt(hex.slice(i, i + 2), 16);
  }
  return out;
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

// ---------------------------------------------------------------------------
// Main
// ---------------------------------------------------------------------------

export default {
  async fetch(req: Request, env: Env): Promise<Response> {
    const url = new URL(req.url);
    const path = url.pathname;

    if (req.method === "POST" && path === "/store") {
      return handleStore(req, env);
    }
    if (req.method === "GET" && path === "/pending") {
      return handlePending(req, env, url);
    }
    const m = path.match(/^\/message\/([a-f0-9]{32})$/);
    if (req.method === "DELETE" && m) {
      return handleDelete(req, env, url, m[1]);
    }
    if (req.method === "GET" && path === "/health") {
      return new Response("ok", { status: 200 });
    }
    return new Response("Not found", { status: 404 });
  },
};

// ---------------------------------------------------------------------------
// POST /store
// ---------------------------------------------------------------------------

async function handleStore(req: Request, env: Env): Promise<Response> {
  let body: Record<string, unknown>;
  try {
    body = (await req.json()) as Record<string, unknown>;
  } catch {
    return new Response("Bad JSON", { status: 400 });
  }

  const {
    relay_id_hash,
    message_id,
    ciphertext_b64,
    nonce_b64,
    sender_username,
    sender_public_key_b64,
    timestamp,
    ttl_s,
  } = body;

  // Validate required fields
  if (
    typeof relay_id_hash !== "string" ||
    !/^[a-f0-9]{64}$/.test(relay_id_hash)
  )
    return new Response("Invalid relay_id_hash", { status: 400 });

  if (typeof message_id !== "string" || !/^[a-f0-9]{32}$/.test(message_id))
    return new Response("Invalid message_id", { status: 400 });

  if (
    typeof ciphertext_b64 !== "string" ||
    ciphertext_b64.length === 0 ||
    ciphertext_b64.length > MAX_CT_LEN
  )
    return new Response("Invalid or oversized ciphertext", { status: 400 });

  if (typeof nonce_b64 !== "string" || nonce_b64.length === 0)
    return new Response("Missing nonce", { status: 400 });

  // Inbox size cap
  const inboxKey = `inbox:${relay_id_hash}`;
  const inboxRaw = await env.RELAY_STORE.get(inboxKey);
  const inbox: string[] = inboxRaw ? (JSON.parse(inboxRaw) as string[]) : [];

  // Deduplicate
  if (inbox.includes(message_id)) {
    return json({ ok: true, duplicate: true });
  }
  if (inbox.length >= MAX_INBOX) {
    return new Response("Inbox full", { status: 429 });
  }

  const ttl = Math.min(
    Math.max(typeof ttl_s === "number" ? ttl_s : TTL_SECONDS, 300),
    TTL_SECONDS
  );

  const msg: StoredMessage = {
    relay_id_hash,
    message_id,
    ciphertext_b64,
    nonce_b64,
    sender_username: String(sender_username ?? "").slice(0, 64),
    sender_public_key_b64: String(sender_public_key_b64 ?? "").slice(0, 64),
    timestamp: typeof timestamp === "number" ? timestamp : Date.now() / 1000,
    stored_at: Date.now() / 1000,
  };

  // Write message, then update inbox index. Both use the same TTL so
  // the index never expires before the messages it references.
  await env.RELAY_STORE.put(`msg:${message_id}`, JSON.stringify(msg), {
    expirationTtl: ttl,
  });
  inbox.push(message_id);
  // Always refresh the inbox index TTL to the full window so adding a new
  // message doesn't inherit the (shorter) expiry of the existing index.
  await env.RELAY_STORE.put(inboxKey, JSON.stringify(inbox), {
    expirationTtl: TTL_SECONDS,
  });

  return json({ ok: true });
}

// ---------------------------------------------------------------------------
// GET /pending?relay_id=<64-hex>
// ---------------------------------------------------------------------------

async function handlePending(
  _req: Request,
  env: Env,
  url: URL
): Promise<Response> {
  const relayId = url.searchParams.get("relay_id");
  if (!relayId || !/^[a-f0-9]{64}$/.test(relayId)) {
    return new Response("Missing or invalid relay_id", { status: 400 });
  }

  // Ownership proof: compute SHA256(relay_id) → matches relay_id_hash in stored msgs
  const hashHex = await sha256Hex(fromHex(relayId));
  const inboxKey = `inbox:${hashHex}`;
  const inboxRaw = await env.RELAY_STORE.get(inboxKey);
  if (!inboxRaw) {
    return json([]);
  }

  const messageIds = JSON.parse(inboxRaw) as string[];
  const messages: Omit<StoredMessage, "relay_id_hash" | "stored_at">[] = [];
  const stale: string[] = [];

  for (const id of messageIds) {
    const raw = await env.RELAY_STORE.get(`msg:${id}`);
    if (!raw) {
      stale.push(id); // expired out of KV
      continue;
    }
    const m = JSON.parse(raw) as StoredMessage;
    // Extra safety: confirm message belongs to this inbox
    if (m.relay_id_hash !== hashHex) {
      stale.push(id);
      continue;
    }
    messages.push({
      message_id: m.message_id,
      ciphertext_b64: m.ciphertext_b64,
      nonce_b64: m.nonce_b64,
      sender_username: m.sender_username,
      sender_public_key_b64: m.sender_public_key_b64,
      timestamp: m.timestamp,
    });
  }

  // Prune stale references from inbox (fire-and-forget, non-blocking)
  if (stale.length > 0) {
    const remaining = messageIds.filter((id) => !stale.includes(id));
    if (remaining.length > 0) {
      env.RELAY_STORE.put(inboxKey, JSON.stringify(remaining), {
        expirationTtl: TTL_SECONDS,
      });
    } else {
      env.RELAY_STORE.delete(inboxKey);
    }
  }

  return json(messages);
}

// ---------------------------------------------------------------------------
// DELETE /message/:id?relay_id=<64-hex>
// ---------------------------------------------------------------------------

async function handleDelete(
  _req: Request,
  env: Env,
  url: URL,
  messageId: string
): Promise<Response> {
  const relayId = url.searchParams.get("relay_id");
  if (!relayId || !/^[a-f0-9]{64}$/.test(relayId)) {
    return new Response("Missing or invalid relay_id", { status: 400 });
  }

  const hashHex = await sha256Hex(fromHex(relayId));
  const raw = await env.RELAY_STORE.get(`msg:${messageId}`);
  if (!raw) {
    return new Response("Not found", { status: 404 });
  }

  const m = JSON.parse(raw) as StoredMessage;
  if (m.relay_id_hash !== hashHex) {
    return new Response("Forbidden", { status: 403 });
  }

  // Delete message
  await env.RELAY_STORE.delete(`msg:${messageId}`);

  // Remove from inbox index
  const inboxKey = `inbox:${hashHex}`;
  const inboxRaw = await env.RELAY_STORE.get(inboxKey);
  if (inboxRaw) {
    const inbox = (JSON.parse(inboxRaw) as string[]).filter(
      (id) => id !== messageId
    );
    if (inbox.length > 0) {
      await env.RELAY_STORE.put(inboxKey, JSON.stringify(inbox), {
        expirationTtl: TTL_SECONDS,
      });
    } else {
      await env.RELAY_STORE.delete(inboxKey);
    }
  }

  return json({ ok: true });
}
