/**
 * Dual rate limiting: per-device + per-IP, using Cloudflare KV.
 *
 * Strategy: sliding window counter.
 * - Device key: `rl:d:<deviceId>` — 30 req/min
 * - IP key:     `rl:ip:<ip>`      — 60 req/min
 *
 * Both limits must pass. KV is eventually consistent but sufficient
 * for soft rate limiting.
 */

/** Per-device: requests allowed per window */
const DEVICE_RATE_LIMIT = 30;

/** Per-IP: requests allowed per window (higher to allow shared IPs) */
const IP_RATE_LIMIT = 60;

/** Window duration in seconds */
const WINDOW_SECONDS = 60;

export interface RateLimitResult {
  allowed: boolean;
  retryAfterSeconds: number;
}

interface RateLimitEntry {
  count: number;
  windowStart: number; // Unix timestamp in seconds
}

/**
 * Check both device-id and IP rate limits.
 * Request is allowed only if both pass.
 */
export async function handleRateLimit(
  kv: KVNamespace,
  deviceId: string,
  ip: string
): Promise<RateLimitResult> {
  const [deviceResult, ipResult] = await Promise.all([
    checkLimit(kv, `rl:d:${deviceId}`, DEVICE_RATE_LIMIT),
    checkLimit(kv, `rl:ip:${ip}`, IP_RATE_LIMIT),
  ]);

  if (!deviceResult.allowed) return deviceResult;
  if (!ipResult.allowed) return ipResult;
  return { allowed: true, retryAfterSeconds: 0 };
}

/**
 * Check IP-only rate limit (no device ID).
 * Used by registration endpoint where device ID is not yet trusted.
 */
export async function checkIpRateLimit(
  kv: KVNamespace,
  ip: string
): Promise<RateLimitResult> {
  return checkLimit(kv, `rl:ip:${ip}`, IP_RATE_LIMIT);
}

async function checkLimit(
  kv: KVNamespace,
  key: string,
  limit: number
): Promise<RateLimitResult> {
  const now = Math.floor(Date.now() / 1000);

  let entry: RateLimitEntry | null = null;
  try {
    const raw = await kv.get(key);
    if (raw) {
      entry = JSON.parse(raw) as RateLimitEntry;
    }
  } catch {
    // KV read failure — allow the request (fail open)
    return { allowed: true, retryAfterSeconds: 0 };
  }

  // Start new window if expired or missing
  if (!entry || now - entry.windowStart >= WINDOW_SECONDS) {
    entry = { count: 1, windowStart: now };
  } else {
    entry.count++;
  }

  // Check limit
  if (entry.count > limit) {
    const retryAfter = WINDOW_SECONDS - (now - entry.windowStart);
    return { allowed: false, retryAfterSeconds: Math.max(retryAfter, 1) };
  }

  // Write updated counter (TTL = 2× window to auto-cleanup)
  try {
    await kv.put(key, JSON.stringify(entry), {
      expirationTtl: WINDOW_SECONDS * 2,
    });
  } catch {
    // KV write failure — still allow the request
  }

  return { allowed: true, retryAfterSeconds: 0 };
}
