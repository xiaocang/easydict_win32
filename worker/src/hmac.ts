/**
 * HMAC-SHA256 utilities for device ID signing and verification.
 *
 * Uses the Web Crypto API (crypto.subtle) available in Cloudflare Workers.
 * The HMAC is deterministic and stateless — no DB lookups needed.
 */

/**
 * Import the signing key as a CryptoKey for HMAC-SHA256.
 */
async function importKey(secret: string): Promise<CryptoKey> {
  const encoder = new TextEncoder();
  return crypto.subtle.importKey(
    "raw",
    encoder.encode(secret),
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["sign", "verify"]
  );
}

/**
 * Sign a device ID with HMAC-SHA256.
 * @returns Hex-encoded token string.
 */
export async function signDeviceId(
  deviceId: string,
  secret: string
): Promise<string> {
  const key = await importKey(secret);
  const encoder = new TextEncoder();
  const signature = await crypto.subtle.sign(
    "HMAC",
    key,
    encoder.encode(deviceId)
  );
  return bufferToHex(signature);
}

/**
 * Verify a device token against a device ID using constant-time comparison.
 * Uses crypto.subtle.verify to avoid timing attacks.
 */
export async function verifyDeviceToken(
  deviceId: string,
  token: string,
  secret: string
): Promise<boolean> {
  const key = await importKey(secret);
  const encoder = new TextEncoder();

  let tokenBytes: Uint8Array;
  try {
    tokenBytes = hexToBuffer(token);
  } catch {
    return false; // Invalid hex
  }

  return crypto.subtle.verify(
    "HMAC",
    key,
    tokenBytes,
    encoder.encode(deviceId)
  );
}

function bufferToHex(buffer: ArrayBuffer): string {
  const bytes = new Uint8Array(buffer);
  let hex = "";
  for (const b of bytes) {
    hex += b.toString(16).padStart(2, "0");
  }
  return hex;
}

function hexToBuffer(hex: string): Uint8Array {
  if (hex.length % 2 !== 0) {
    throw new Error("Invalid hex string");
  }
  const bytes = new Uint8Array(hex.length / 2);
  for (let i = 0; i < hex.length; i += 2) {
    bytes[i / 2] = parseInt(hex.substring(i, i + 2), 16);
  }
  return bytes;
}
