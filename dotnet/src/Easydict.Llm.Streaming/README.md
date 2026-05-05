# Easydict.Llm.Streaming

Lightweight Server-Sent Events (SSE) parser for OpenAI-compatible LLM chat
completion streams. Targets `net8.0` and depends only on `System.Text.Json`.

## Features

- Async streaming parser exposed as `IAsyncEnumerable<string>`.
- Handles standard `data: {...}` lines and the `data: [DONE]` terminator.
- Skips comments, blank lines, and malformed JSON without throwing.
- Cancellation-aware via `CancellationToken`.

## Usage

```csharp
using Easydict.Llm.Streaming;

await foreach (var chunk in SseParser.ParseStreamAsync(httpResponseStream, cancellationToken))
{
    Console.Write(chunk);
}
```

The parser yields each `choices[0].delta.content` value as it arrives.
Other event metadata (e.g. `event:`, `id:`, `retry:`) is ignored — the parser
is intentionally minimal and OpenAI-shape specific.

`ChatRole` and `ChatMessage` types are also provided as small helpers when
building chat completion request bodies.

## License

GPL-3.0-only.
