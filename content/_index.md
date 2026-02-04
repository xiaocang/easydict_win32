+++
title = "Easydict for Windows"
description = "Easy to look up words or translate text"
template = "index.html"

[extra]
screenshot = "img/overview.png"
+++

This is a Windows port of [Easydict](https://github.com/tisfeng/Easydict), originally a macOS translation dictionary app. The project was developed using **Vibe Coding** - AI-assisted programming to migrate the Swift/SwiftUI codebase to .NET 8 + WinUI 3.

While the feature set is not yet complete compared to the macOS version, this port fills the gap for Windows users who want a convenient translation tool with global hotkey support and multiple translation services.

## Features


- **Multiple Translation Services**
  - Google Translate (free, no API key required)
  - Google Dict (rich dictionary: phonetics, definitions, examples)
  - Bing Translate (free, no API key required)
  - DeepL (supports Free/Pro API, Traditional Chinese supported)
  - OpenAI (GPT-4o, GPT-4o-mini, etc.)
  - Gemini (Google AI, including Gemini 2.5 models)
  - DeepSeek
  - Groq (fast LLM inference)
  - Zhipu AI
  - GitHub Models (free)
  - Doubao (ByteDance translation-specialized model)
  - Caiyun (彩云小译, Traditional Chinese supported)
  - NiuTrans (小牛翻译, 450+ languages, Traditional Chinese supported)
  - Linguee Dictionary (with context examples)
  - Ollama (local LLM, default: llama3.2)
  - BuiltIn AI (free, powered by Groq)
  - Custom OpenAI-compatible services

- **LLM Streaming Translation** - Real-time display of translation results

- **Multiple Window Modes**
  - Main Window - Full translation interface
  - Mini Window - Compact floating window
  - Fixed Window - Persistent translation window

- **Global Hotkeys**
  - `Ctrl+Alt+T` - Show/hide main window
  - `Ctrl+Alt+D` - Translate clipboard content
  - `Ctrl+Alt+M` - Show mini window (copies selection and translates when available)
  - `Ctrl+Alt+F` - Show fixed window
  - `Ctrl+Alt+Shift+M` - Toggle mini window visibility
  - `Ctrl+Alt+Shift+F` - Toggle fixed window visibility

- **System Tray** - Minimize to tray, run in background

- **Clipboard Monitoring** - Auto-translate copied text

- **HTTP Proxy Support** - Configure proxy server

- **High DPI Support** - Per-Monitor V2 DPI awareness

- **Mouse Selection Translate** - Select text in any app (drag, double-click, or triple-click) and click the floating pop button to translate instantly in Mini Window

- **Dark/Light Theme** - Follows system theme

- **TTS (Text-to-Speech)** - Play source and translated text using Windows Speech Synthesis

## Installation

### System Requirements

- Windows 10 version 2004 (build 19041) or later
- x64 or ARM64 processor

### Download

Download from the [Releases](https://github.com/xiaocang/easydict_win32/releases) page.

#### Portable Version (Recommended)

**File:** `easydict_win32-vX.Y.Z-x64.zip`

- No installation required - extract and run
- No administrator privileges needed
- Self-contained (.NET runtime included)
- First run may trigger Windows SmartScreen warning - click "More info" → "Run anyway"

```powershell
# Extract and run
Expand-Archive easydict_win32-v1.0.0-x64.zip -DestinationPath Easydict
.\Easydict\Easydict.WinUI.exe
```

#### Verify Download (Optional)

Each release includes SHA256 checksums for verification.

```bash
# Linux/macOS/WSL
sha256sum -c checksums-x64.sha256 --ignore-missing

# PowerShell
$expected = (Get-Content checksums-x64.sha256 | Select-String "easydict_win32").ToString().Split()[0]
$actual = (Get-FileHash easydict_win32-v1.0.0-x64.zip -Algorithm SHA256).Hash.ToLower()
if ($expected -eq $actual) { "OK" } else { "FAILED" }
```

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
