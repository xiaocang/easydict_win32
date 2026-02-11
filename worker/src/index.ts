/**
 * Easydict AI Proxy Worker
 *
 * Routes OpenAI-compatible chat completion requests to GLM (Zhipu) or Groq
 * based on the model name. Applies per-device rate limiting via X-Device-Id.
 *
 * Client sends:
 *   POST /v1/chat/completions
 *   Headers: Authorization: Bearer <embedded-key>, X-Device-Id: <guid>
 *   Body: standard OpenAI chat completion request
 *
 * Worker:
 *   1. Validates the request (method, path, auth)
 *   2. Checks per-device rate limit (KV-backed sliding window)
 *   3. Routes to upstream provider (GLM or Groq) based on model
 *   4. Streams SSE response back to client
 */

import { handleRateLimit, RateLimitResult } from "./rate-limit";
import { resolveUpstream, UpstreamConfig } from "./routing";

export interface Env {
  RATE_LIMIT: KVNamespace;
  GLM_API_KEY: string;
  GROQ_API_KEY: string;
  /** Optional: override the embedded API key check. If unset, auth is skipped. */
  PROXY_API_KEY?: string;
}

const CORS_HEADERS: Record<string, string> = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Methods": "POST, OPTIONS",
  "Access-Control-Allow-Headers": "Content-Type, Authorization, X-Device-Id",
};

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    // CORS preflight
    if (request.method === "OPTIONS") {
      return new Response(null, { status: 204, headers: CORS_HEADERS });
    }

    // Only POST /v1/chat/completions
    const url = new URL(request.url);
    if (request.method !== "POST" || url.pathname !== "/v1/chat/completions") {
      return jsonError(404, "Not found");
    }

    // Auth check (optional — only if PROXY_API_KEY is configured)
    if (env.PROXY_API_KEY) {
      const auth = request.headers.get("Authorization") ?? "";
      const token = auth.startsWith("Bearer ") ? auth.slice(7) : "";
      if (token !== env.PROXY_API_KEY) {
        return jsonError(401, "Unauthorized");
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

    // Rate limiting (per device)
    const deviceId = request.headers.get("X-Device-Id") ?? "anonymous";
    const rateResult = await handleRateLimit(env.RATE_LIMIT, deviceId);
    if (!rateResult.allowed) {
      return jsonError(429, "Rate limit exceeded. Please try again later.", {
        "Retry-After": String(rateResult.retryAfterSeconds),
      });
    }

    // Forward to upstream
    return proxyToUpstream(upstream, body);
  },
};

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
