# Easydict Win32

Windows port of Easydict (macOS translation dictionary app) built with .NET 8 + WinUI 3.

## Tech Stack

- .NET 8, C# 12
- WinUI 3 (Windows App SDK)
- xUnit + FluentAssertions for testing

## Project Structure

```
easydict_win32/
├── dotnet/                              # .NET solution root
│   ├── src/
│   │   ├── Easydict.WinUI/              # Main WinUI 3 application
│   │   ├── Easydict.TranslationService/ # Translation service library
│   │   └── Easydict.SidecarClient/      # IPC client library
│   ├── tests/
│   │   ├── Easydict.TranslationService.Tests/
│   │   └── Easydict.WinUI.Tests/
│   ├── Easydict.Win32.sln               # Solution file
│   └── Makefile
├── sidecar_mock/                        # Mock sidecar for testing
├── screenshot/                          # Screenshots for README
└── README.md
```

## Build Commands

All commands should be run from the `dotnet/` directory.

```bash
# Build Debug
dotnet build src/Easydict.WinUI/Easydict.WinUI.csproj -c Debug

# Build Release
dotnet build src/Easydict.WinUI/Easydict.WinUI.csproj -c Release

# Run the app
dotnet run --project src/Easydict.WinUI/Easydict.WinUI.csproj

# Run all tests
dotnet test Easydict.Win32.sln

# Publish
dotnet publish src/Easydict.WinUI/Easydict.WinUI.csproj -c Release -o ./publish
```

## Key Features

- Multiple translation services (Google, DeepL, OpenAI, Gemini, DeepSeek, Groq, etc.)
- LLM streaming translation
- Multiple window modes (Main, Mini, Fixed)
- Global hotkeys (Ctrl+Alt+T, Ctrl+Alt+D, Ctrl+Alt+M)
- System tray support
- Clipboard monitoring
- Dark/Light theme support
