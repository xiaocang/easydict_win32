# Lookup latency investigation

This is a manual investigation aid for #247. It does not reproduce the original report by itself, and it does not measure rendering or detect the root cause.

On Windows, run `python scripts/lookup_latency_mock.py` from the repository root. Configure a Custom OpenAI service with endpoint `http://127.0.0.1:18765/v1/chat/completions`, a dummy API key, and a dummy model. Select the service for the lookup window. The server binds only to loopback and logs request arrival without logging the selected text or credentials. It is for local testing only.

For a baseline, compare a direct call to the mock with a lookup in Easydict. Record app version, Windows version, enabled services, source/target language settings, selected text, streaming setting, and cold/warm runs. Use a stopwatch or screen recording for selection, window display, first visible result, and completion. Correlate request arrival with the mock's console clock. Do not publish sensitive selected text.

Repeat with explicit source language and auto detection, using a fresh English phrase each time to avoid detection cache hits. The current mini-window code waits for automatic language detection before sending translation requests. Detection can use online providers and has a five-second total budget; this is a hypothesis to measure, not proof of the reported root cause. Explicit source selection can change target selection semantics, so compare the chosen target too.

Restart the mock for each case:

| Command option | Scenario to observe |
| --- | --- |
| none | Immediate successful result |
| `--delay 3` | Slow response headers |
| `--chunk-delay 3` | Delayed stream content, when streaming is enabled |
| `--mode empty` | Empty response |
| `--mode error` | HTTP 500 error |

For each case verify the displayed state, request count, time to first content, and that a subsequent lookup still works. Also rapidly perform a second lookup while the first is delayed, and check that an old result does not replace the newer one. Test multiple enabled services independently. This mock implements only the chat-completions subset and does not simulate every local provider or network failure.
