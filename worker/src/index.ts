/**
 * Easydict AI Proxy Worker
 *
 * Routes OpenAI-compatible chat completion requests to GLM (Zhipu) or Groq
 * based on the model name. Dual rate limiting: per-device (X-Device-Id) + per-IP.
 *
 * Client sends:
 *   POST /v1/chat/completions
 *   Headers: Authorization: Bearer <embedded-key>, X-Device-Id: <guid>, X-Device-Token: <hmac>
 *   Body: standard OpenAI chat completion request
 *
 *   POST /v1/device/register
 *   Headers: Authorization: Bearer <embedded-key>, X-Device-Id: <hex-device-id>
 *   Returns: { "device_id": "...", "device_token": "..." }
 *
 * Worker:
 *   1. Validates the request (method, path, auth)
 *   2. Checks per-device rate limit (KV-backed sliding window)
 *   3. Verifies device token (HMAC-SHA256) — grace period: allows missing token
 *   4. Routes to upstream provider (GLM or Groq) based on model
 *   5. Streams SSE response back to client
 */

import { handleRateLimit, checkIpRateLimit } from "./rate-limit";
import { resolveUpstream, UpstreamConfig } from "./routing";
import { signDeviceId, verifyDeviceToken } from "./hmac";

export interface Env {
  RATE_LIMIT: KVNamespace;
  GLM_API_KEY: string;
  GROQ_API_KEY: string;
  /** Optional: override the embedded API key check. If unset, auth is skipped. */
  PROXY_API_KEY?: string;
  /** Secret key for HMAC-SHA256 device token signing. */
  DEVICE_SIGNING_KEY?: string;
}

const CORS_HEADERS: Record<string, string> = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Methods": "POST, OPTIONS",
  "Access-Control-Allow-Headers":
    "Content-Type, Authorization, X-Device-Id, X-Device-Token",
};

/** Device ID validation: 16-128 lowercase hex characters. */
const DEVICE_ID_RE = /^[0-9a-f]{16,128}$/;

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    // CORS preflight
    if (request.method === "OPTIONS") {
      return new Response(null, { status: 204, headers: CORS_HEADERS });
    }

    if (request.method !== "POST") {
      return jsonError(404, "Not found");
    }

    const url = new URL(request.url);

    // Route dispatch
    switch (url.pathname) {
      case "/v1/device/register":
        return handleRegister(request, env);
      case "/v1/chat/completions":
        return handleChat(request, env);
      default:
        return jsonError(404, "Not found");
    }
  },
};

/**
 * POST /v1/device/register
 * Signs a device ID with HMAC-SHA256 and returns the token.
 * Requires Bearer auth and a valid hex device ID.
 */
async function handleRegister(request: Request, env: Env): Promise<Response> {
  // Auth check
  if (env.PROXY_API_KEY) {
    const auth = request.headers.get("Authorization") ?? "";
    const token = auth.startsWith("Bearer ") ? auth.slice(7) : "";
    if (token !== env.PROXY_API_KEY) {
      return jsonError(401, "Unauthorized");
    }
  }

  // Require DEVICE_SIGNING_KEY
  if (!env.DEVICE_SIGNING_KEY) {
    return jsonError(500, "Device signing not configured");
  }

  // Validate X-Device-Id header
  const deviceId = request.headers.get("X-Device-Id") ?? "";
  if (!DEVICE_ID_RE.test(deviceId)) {
    return jsonError(
      400,
      "Invalid X-Device-Id: must be 16-128 lowercase hex characters"
    );
  }

  // IP-only rate limiting (device ID not yet trusted)
  const ip = request.headers.get("CF-Connecting-IP") ?? "unknown";
  const rateResult = await checkIpRateLimit(env.RATE_LIMIT, ip);
  if (!rateResult.allowed) {
    return jsonError(429, "Rate limit exceeded. Please try again later.", {
      "Retry-After": String(rateResult.retryAfterSeconds),
    });
  }

  // Sign device ID
  const deviceToken = await signDeviceId(deviceId, env.DEVICE_SIGNING_KEY);

  const headers: Record<string, string> = {
    ...CORS_HEADERS,
    "Content-Type": "application/json",
  };

  return new Response(
    JSON.stringify({ device_id: deviceId, device_token: deviceToken }),
    { status: 200, headers }
  );
}

/**
 * POST /v1/chat/completions
 * Proxies chat completion requests to upstream providers.
 * Phase 1 (grace period): validates token if present, allows if missing.
 */
async function handleChat(request: Request, env: Env): Promise<Response> {
  // Auth check (optional — only if PROXY_API_KEY is configured)
  if (env.PROXY_API_KEY) {
    const auth = request.headers.get("Authorization") ?? "";
    const token = auth.startsWith("Bearer ") ? auth.slice(7) : "";
    if (token !== env.PROXY_API_KEY) {
      return jsonError(401, "Unauthorized");
    }
  }

  // Device token verification (Phase 1: grace period — allow missing token)
  const deviceId = request.headers.get("X-Device-Id") ?? "anonymous";
  const deviceToken = request.headers.get("X-Device-Token");

  if (deviceToken && env.DEVICE_SIGNING_KEY) {
    const valid = await verifyDeviceToken(
      deviceId,
      deviceToken,
      env.DEVICE_SIGNING_KEY
    );
    if (!valid) {
      return jsonError(403, "Invalid device token");
    }
  }

  // Parse request body
  let body: Record<string, unknown>;
  try {
    body = (await request.json()) as Record<string, unknown>;
  } catch {
    return jsonError(400, "Invalid JSON body");
  }

  const model = body.model as string | undefined;
  if (!model) {
    return jsonError(400, "Missing 'model' field");
  }

  // Resolve upstream provider
  const upstream = resolveUpstream(model, env);
  if (!upstream) {
    return jsonError(400, `Unsupported model: ${model}`);
  }

  // Dual rate limiting: per-device + per-IP
  const ip = request.headers.get("CF-Connecting-IP") ?? "unknown";
  const rateResult = await handleRateLimit(env.RATE_LIMIT, deviceId, ip);
  if (!rateResult.allowed) {
    return jsonError(429, "Rate limit exceeded. Please try again later.", {
      "Retry-After": String(rateResult.retryAfterSeconds),
    });
  }

  // Forward to upstream
  return proxyToUpstream(upstream, body);
}

/**
 * Proxy the request to the upstream provider and stream the response back.
 */
async function proxyToUpstream(
  upstream: UpstreamConfig,
  body: Record<string, unknown>
): Promise<Response> {
  const upstreamResponse = await fetch(upstream.endpoint, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Authorization: `Bearer ${upstream.apiKey}`,
    },
    body: JSON.stringify(body),
  });

  // Stream the response back as-is (preserves SSE for streaming requests)
  const responseHeaders = new Headers(CORS_HEADERS);
  const contentType = upstreamResponse.headers.get("Content-Type");
  if (contentType) {
    responseHeaders.set("Content-Type", contentType);
  }

  return new Response(upstreamResponse.body, {
    status: upstreamResponse.status,
    headers: responseHeaders,
  });
}

function jsonError(
  status: number,
  message: string,
  extraHeaders?: Record<string, string>
): Response {
  const headers: Record<string, string> = {
    ...CORS_HEADERS,
    "Content-Type": "application/json",
    ...extraHeaders,
  };
  return new Response(
    JSON.stringify({
      error: { message, type: "proxy_error", code: status },
    }),
    { status, headers }
  );
}
