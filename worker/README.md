# Easydict AI Proxy Worker

Cloudflare Worker that proxies OpenAI-compatible chat completion requests to upstream LLM providers (Zhipu GLM, Groq). Used by the Easydict Win32 Built-in AI service.

## Architecture

```
Client (Easydict Win32)
  ├── POST /v1/chat/completions
  ├── Authorization: Bearer <embedded-key>
  └── X-Device-Id: <hardware-id>
        │
        ▼
  Cloudflare Worker
  ├── Auth check (optional)
  ├── Rate limit (per-device + per-IP, KV-backed)
  └── Route by model name
        │
        ├── glm-4-flash* ──────► Zhipu GLM API
        └── llama-3.* ─────────► Groq API
```

## Setup

```bash
cd worker

# Install dependencies
npm install

# Set secrets (not committed to repo)
npx wrangler secret put GLM_API_KEY
npx wrangler secret put GROQ_API_KEY
npx wrangler secret put PROXY_API_KEY  # optional: validate client auth

# Create KV namespace for rate limiting
npx wrangler kv namespace create RATE_LIMIT
# Then update the KV namespace ID in wrangler.toml

# Local dev
npm run dev

# Deploy
npm run deploy
```

## Configuration

| Secret | Required | Description |
|--------|----------|-------------|
| `GLM_API_KEY` | Yes | Zhipu GLM API key from open.bigmodel.cn |
| `GROQ_API_KEY` | Yes | Groq API key from console.groq.com |
| `PROXY_API_KEY` | No | If set, clients must send this as Bearer token |

## Rate Limiting

Dual rate limiting — both must pass:

| Dimension | Limit | Key |
|-----------|-------|-----|
| Per device | 30 req/min | `X-Device-Id` header (hardware-bound) |
| Per IP | 60 req/min | `CF-Connecting-IP` (unforgeable) |

- Sliding window counters stored in Cloudflare KV
- Fails open (allows request) if KV is unavailable
- Anonymous requests (no Device-Id) share a single device counter
- IP limit is higher to accommodate shared networks (NAT, VPN)
