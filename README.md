# easydict_win32

[![CI](https://github.com/xiaocang/easydict_win32/actions/workflows/ci.yml/badge.svg)](https://github.com/xiaocang/easydict_win32/actions/workflows/ci.yml)

## Introduction

This is a Windows port of [Easydict](https://github.com/tisfeng/Easydict), originally a macOS translation dictionary app. The project was developed using **Vibe Coding** - AI-assisted programming to migrate the Swift/SwiftUI codebase to .NET 8 + WinUI 3.

While the feature set is not yet complete compared to the macOS version, this port fills the gap for Windows users who want a convenient translation tool with global hotkey support and multiple translation services.

## Tech Stack

- **.NET 8** - Runtime framework
- **WinUI 3 (Windows App SDK)** - Modern Windows UI framework
- **C# 12** - Programming language
- **xUnit + FluentAssertions** - Unit testing

## Features

### Implemented

- **Multiple Translation Services**
  - Google Translate (free, no API key required)
  - DeepL (supports Free/Pro API)
  - OpenAI (GPT-4o, GPT-4o-mini, etc.)
  - Gemini (Google AI)
  - DeepSeek
  - Groq (fast LLM inference)
  - Zhipu AI
  - GitHub Models (free)
  - Custom OpenAI-compatible services

- **LLM Streaming Translation** - Real-time display of translation results

- **Multiple Window Modes**
  - Main Window - Full translation interface
  - Mini Window - Compact floating window
  - Fixed Window - Persistent translation window

- **Global Hotkeys**
  - `Ctrl+Alt+T` - Show/hide main window
  - `Ctrl+Alt+D` - Translate clipboard content
  - `Ctrl+Alt+M` - Show/hide mini window

- **System Tray** - Minimize to tray, run in background

- **Clipboard Monitoring** - Auto-translate copied text

- **HTTP Proxy Support** - Configure proxy server

- **High DPI Support** - Per-Monitor V2 DPI awareness

- **Dark/Light Theme** - Follows system theme

### Screenshots

| Main Window | Mini Window |
|-------------|-------------|
| ![Main Window](screenshot/Snipaste_2026-01-20_19-55-25.png) | ![Mini Window](screenshot/Snipaste_2026-01-19_23-31-38.png) |

## Installation

### System Requirements

- Windows 10 version 1809 (build 17763) or later
- .NET 8.0 Runtime

### Download

Download the latest ZIP package from the [Releases](https://github.com/xiaocang/easydict_win32/releases) page, extract it, and run `Easydict.WinUI.exe`.

### Build from Source

```powershell
# Clone repository
git clone https://github.com/xiaocang/easydict_win32.git
cd easydict_win32/dotnet

# Build
dotnet build src/Easydict.WinUI/Easydict.WinUI.csproj -c Release

# Run
dotnet run --project src/Easydict.WinUI/Easydict.WinUI.csproj
```

## TODO

### High Priority

- [ ] **Ollama Support** - Local LLM service (localhost:11434)
- [ ] **BuiltIn AI** - Built-in free translation service
- [ ] **TTS (Text-to-Speech)** - Windows Speech Synthesis API
- [ ] **Selection Translation** - Auto-detect selected text

### Medium Priority

- [ ] **More Translation Services**
  - [ ] Caiyun
  - [ ] Volcano
  - [ ] NiuTrans
  - [ ] Doubao
  - [ ] Linguee Dictionary

- [ ] **AI Tools**
  - [ ] Text Polishing
  - [ ] Text Summarization

- [ ] **More Hotkeys**
  - [ ] Selection translation hotkey
  - [ ] Polish and replace
  - [ ] Translate and replace

### Low Priority

- [ ] **Dictionary Mode** - Word definitions, pronunciation
- [ ] **Smart Query** - Auto-select translation mode based on text type
- [ ] **Multi-language UI** - UI localization
- [ ] **Auto Update** - Check and install updates

## Comparison with macOS Version

| Feature | macOS | Windows |
|---------|-------|---------|
| Translation Services | 25+ | 10 |
| OCR Screenshot Translation | Yes | No |
| TTS | Yes | Planned |
| Selection Translation | Yes | Planned |
| Window Types | 3 | 3 |
| Global Hotkeys | 10+ | 3 |
| LLM Streaming | Yes | Yes |

## License

GPL-3.0 - For learning and communication purposes only. License and copyright notice must be included when using source code.

## Acknowledgements

- [Easydict](https://github.com/tisfeng/Easydict) - Original macOS version
- [Windows App SDK](https://github.com/microsoft/WindowsAppSDK) - WinUI 3 framework

---

*This project was developed using Vibe Coding, with AI-assisted programming by Claude (Anthropic).*
