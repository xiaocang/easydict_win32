/**
 * Upstream provider routing based on model name.
 */

import type { Env } from "./index";

export interface UpstreamConfig {
  endpoint: string;
  apiKey: string;
}

/** GLM (Zhipu AI) endpoint */
const GLM_ENDPOINT = "https://open.bigmodel.cn/api/paas/v4/chat/completions";

/** Groq endpoint */
const GROQ_ENDPOINT = "https://api.groq.com/openai/v1/chat/completions";

/**
 * Model → provider mapping.
 * Must stay in sync with BuiltInAIService.ModelProviderMap on the client.
 */
const MODEL_PROVIDER: Record<string, "glm" | "groq"> = {
  "glm-4-flash": "glm",
  "glm-4-flash-250414": "glm",
  "llama-3.3-70b-versatile": "groq",
  "llama-3.1-8b-instant": "groq",
};

/**
 * Resolve model name to upstream endpoint and API key.
 * Returns null if the model is not supported.
 */
export function resolveUpstream(
  model: string,
  env: Env
): UpstreamConfig | null {
  const provider = MODEL_PROVIDER[model];
  if (!provider) return null;

  switch (provider) {
    case "glm":
      return { endpoint: GLM_ENDPOINT, apiKey: env.GLM_API_KEY };
    case "groq":
      return { endpoint: GROQ_ENDPOINT, apiKey: env.GROQ_API_KEY };
  }
}
