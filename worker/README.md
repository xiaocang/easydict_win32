# Easydict AI Proxy Worker

Cloudflare Worker that proxies OpenAI-compatible chat completion requests to upstream LLM providers (Zhipu GLM, Groq). Used by the Easydict Win32 Built-in AI service.

## Architecture

```
Client (Easydict Win32)
  ├── POST /v1/chat/completions
  ├── Authorization: Bearer <embedded-key>
  └── X-Device-Id: <device-guid>
        │
        ▼
  Cloudflare Worker
  ├── Auth check (optional)
  ├── Rate limit (30 req/min per device, KV-backed)
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

- **30 requests per minute** per `X-Device-Id`
- Sliding window counter stored in Cloudflare KV
- Fails open (allows request) if KV is unavailable
- Anonymous requests (no Device-Id) share a single counter
