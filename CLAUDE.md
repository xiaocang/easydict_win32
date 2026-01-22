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
│   └── Easydict.Win32.sln               # Solution file
```

## Build Commands

All commands should be run from the `dotnet/` directory.

### Windows (Native)

```bash
# Build
dotnet build src/Easydict.WinUI/Easydict.WinUI.csproj

# Test
dotnet test Easydict.Win32.sln

# Run
dotnet run --project src/Easydict.WinUI/Easydict.WinUI.csproj
```

### WSL (Windows Subsystem for Linux)

Use `dotnet.exe` to invoke Windows .NET SDK from WSL:

```bash
# Build WinUI app
dotnet.exe build src/Easydict.WinUI/Easydict.WinUI.csproj

# Build library only
dotnet.exe build src/Easydict.TranslationService/Easydict.TranslationService.csproj

# Run TranslationService tests
dotnet.exe test tests/Easydict.TranslationService.Tests/Easydict.TranslationService.Tests.csproj

# Run WinUI tests
dotnet.exe test tests/Easydict.WinUI.Tests/Easydict.WinUI.Tests.csproj
```

**Note**: Icon generation may need the icon file pre-copied for WSL builds due to UNC path limitations.

## Key Features

- Multiple translation services (Google, DeepL, OpenAI, Gemini, DeepSeek, Groq, etc.)
- LLM streaming translation
- Multiple window modes (Main, Mini, Fixed)
- Global hotkeys (Ctrl+Alt+T, Ctrl+Alt+D, Ctrl+Alt+M)
- System tray support
- Clipboard monitoring
- Dark/Light theme support
