<p align="center">
  <img src="screenshot/icon_512x512@2x.png" height="256">
  <h1 align="center">Easydict <sup><sub>for Windows</sub></sup></h1>
  <h4 align="center">Easy to look up words or translate text</h4>
  <p align="center">A Windows port of <a href="https://github.com/tisfeng/Easydict">Easydict</a></p>
</p>

<div align="center">
<a href="./README.md">English</a> &nbsp;&nbsp;|&nbsp;&nbsp; <a href="./README_ZH.md">中文</a>
</div>

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

- **Dark/Light Theme** - Follows system theme

- **TTS (Text-to-Speech)** - Play source and translated text using Windows Speech Synthesis

### Screenshots

![Overview](screenshot/Snipaste_2026-01-26_21-39-35.png)

*Main Window with Mini Window (Quick Translate)*

| Main Window | All Windows | Settings |
|-------------|-------------|----------|
| ![Main Window](screenshot/Snipaste_2026-01-26_21-40-19.png) | ![All Windows](screenshot/Snipaste_2026-01-26_21-41-52.png) | ![Settings](screenshot/Snipaste_2026-01-26_21-40-59.png) |
| Full translation interface | Main + Mini + Fixed windows with hotkey settings | Service configuration |

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

## TODO

### High Priority

- [x] ~~**Doubao**~~ - ByteDance LLM service ✅ **Implemented**
- [x] ~~**Caiyun**~~ - 彩云小译 ✅ **Implemented**
- [x] ~~**NiuTrans**~~ - 小牛翻译 (450+ languages) ✅ **Implemented**
- [x] ~~**Linguee Dictionary**~~ - Dictionary with context examples ✅ **Implemented**
- [x] ~~**TTS (Text-to-Speech)**~~ - Windows Speech Synthesis API ✅ **Implemented**

### Medium Priority

- [ ] **More Translation Services**
  - [ ] Volcano (ByteDance, may overlap with Doubao)

- [ ] **AI Tools**
  - [ ] Text Polishing
  - [ ] Text Summarization

- [ ] **More Hotkeys**
  - [ ] Polish and replace
  - [ ] Translate and replace

### Low Priority

- [ ] **Dictionary Mode** - Word definitions, pronunciation
- [ ] **Smart Query** - Auto-select translation mode based on text type
- [ ] **Multi-language UI** - UI localization
- [ ] **Auto Update** - Check and install updates

### Distribution

- [ ] **Windows Store** - Publish to Microsoft Store
- [ ] **winget** - Publish to Windows Package Manager

## Comparison with macOS Version

| Feature | macOS | Windows |
|---------|-------|---------|
| Translation Services | 25+ | 15 |
| OCR Screenshot Translation | Yes | No |
| TTS | Yes | Yes |
| Selection Translation | Yes | Yes |
| Window Types | 3 | 3 |
| Global Hotkeys | 10+ | 6 |
| LLM Streaming | Yes | Yes |
| Traditional Chinese | Yes | Yes |

## License

GPL-3.0 - For learning and communication purposes only. License and copyright notice must be included when using source code.

## Acknowledgements

- [Easydict](https://github.com/tisfeng/Easydict) - Original macOS version
- [Windows App SDK](https://github.com/microsoft/WindowsAppSDK) - WinUI 3 framework

---

*This project was developed using Vibe Coding, with AI-assisted programming by Claude (Anthropic).*
