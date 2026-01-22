# Easydict.TranslationService

A .NET library for translation services supporting multiple providers with LLM streaming translation support.

## Features

- **Multiple Translation Providers**
  - Google Translate (free, no API key required)
  - DeepL (supports Free/Pro API)
  - OpenAI (GPT-4o, GPT-4o-mini, etc.)
  - Gemini (Google AI)
  - DeepSeek
  - Groq (fast LLM inference)
  - Zhipu AI
  - GitHub Models (free)
  - Ollama (local LLM)
  - Custom OpenAI-compatible services

- **LLM Streaming Translation** - Real-time display of translation results
- **Language Detection** - Automatic source language detection
- **Resilient HTTP Client** - Built-in retry and resilience policies

## Installation

```bash
dotnet add package Easydict.TranslationService
```

## Usage

```csharp
using Easydict.TranslationService;
using Microsoft.Extensions.DependencyInjection;

// Setup dependency injection
var services = new ServiceCollection();
services.AddTranslationServices();
var serviceProvider = services.BuildServiceProvider();

// Get translation manager
var translationManager = serviceProvider.GetRequiredService<ITranslationManager>();

// Translate text
var result = await translationManager.TranslateAsync(
    "Hello, world!",
    sourceLanguage: "en",
    targetLanguage: "zh"
);
```

## Requirements

- .NET 8.0 or later

## License

GPL-3.0 - See [LICENSE](https://github.com/xiaocang/easydict_win32/blob/main/LICENSE) for details.

## Links

- [GitHub Repository](https://github.com/xiaocang/easydict_win32)
- [Original Easydict (macOS)](https://github.com/tisfeng/Easydict)
