/**
 * Per-device rate limiting using Cloudflare KV.
 *
 * Strategy: sliding window counter.
 * - Key: `rl:<deviceId>` → JSON { count, windowStart }
 * - Window: 1 minute
 * - Limit: 30 requests per window
 *
 * KV is eventually consistent but sufficient for soft rate limiting.
 */

/** Requests allowed per window */
const RATE_LIMIT = 30;

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

export async function handleRateLimit(
  kv: KVNamespace,
  deviceId: string
): Promise<RateLimitResult> {
  const key = `rl:${deviceId}`;
  const now = Math.floor(Date.now() / 1000);

  let entry: RateLimitEntry | null = null;
  try {
    const raw = await kv.get(key);
    if (raw) {
      entry = JSON.parse(raw) as RateLimitEntry;
    }
  } catch {
    // KV read failure — allow the request (fail open)
  }

  // Start new window if expired or missing
  if (!entry || now - entry.windowStart >= WINDOW_SECONDS) {
    entry = { count: 1, windowStart: now };
  } else {
    entry.count++;
  }

  // Check limit
  if (entry.count > RATE_LIMIT) {
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
