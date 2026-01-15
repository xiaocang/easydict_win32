# Windows Continuation Context

This document captures the full context for continuing Easydict Windows development on native Windows.

## Project Goal

Bring Easydict to Windows using:
- **UI Layer**: WinUI 3 (Windows App SDK) + .NET 8 (C#)
- **Core Logic**: Reuse existing Swift business logic via a **sidecar process**
- **IPC**: JSON Lines over stdio (no network ports)

Design document: `win32-ui-ag.md`

---

## Milestone Status

| Milestone | Status | Description |
|-----------|--------|-------------|
| **0A** | ✅ DONE | IPC protocol + JSONL codec + mock service + E2E runner (Python) |
| **0B** | ✅ DONE | .NET SidecarClient library - verified on Windows, all 8 E2E tests passing |
| **0C** | ✅ DONE | WinUI 3 App Shell + integrate SidecarClient |
| **1** | ✅ DONE | Real translation path - C# native implementation (Google, DeepL, caching, retry) |
| **2** | 🔜 NEXT | Native integrations (tray, hotkeys, clipboard, settings) |

---

## File Structure

```
win32/
├── sidecar_mock/
│   ├── ipc_mock_service.py    # Mock sidecar (Python, cross-platform)
│   ├── e2e_ipc.py             # Original E2E tests (basic protocol)
│   └── e2e_client.py          # Extended E2E tests (concurrent, timeout, crash)
│
└── dotnet/
    ├── Easydict.Win32.sln     # Solution file
    ├── src/
    │   ├── Easydict.SidecarClient/        # IPC client for sidecar process
    │   │   ├── Easydict.SidecarClient.csproj
    │   │   ├── SidecarClient.cs           # Core client (process mgmt, multiplexing)
    │   │   ├── SidecarClientOptions.cs    # Configuration options
    │   │   ├── SidecarException.cs        # Exception types
    │   │   └── Protocol/
    │   │       ├── IpcRequest.cs          # Request model
    │   │       ├── IpcResponse.cs         # Response/Error model
    │   │       ├── IpcEvent.cs            # Event model (streaming)
    │   │       ├── IpcMessage.cs          # Raw message parser
    │   │       └── JsonLineSerializer.cs  # JSONL serializer
    │   │
    │   ├── Easydict.TranslationService/   # ✅ C# native translation (replaces Swift sidecar)
    │   │   ├── Easydict.TranslationService.csproj
    │   │   ├── ITranslationService.cs     # Translation service interface
    │   │   ├── TranslationManager.cs      # Service orchestration, caching, retry
    │   │   ├── Models/
    │   │   │   ├── Language.cs            # Language enum (60+ languages)
    │   │   │   ├── TranslationRequest.cs  # Request model
    │   │   │   └── TranslationResult.cs   # Result model (record type)
    │   │   └── Services/
    │   │       ├── BaseTranslationService.cs    # Base class with retry logic
    │   │       ├── GoogleTranslateService.cs    # Google Translate (free API)
    │   │       └── DeepLService.cs              # DeepL API
    │   │
    │   └── Easydict.WinUI/                # WinUI 3 App
    │       ├── Easydict.WinUI.csproj
    │       ├── App.xaml / App.xaml.cs
    │       ├── Themes/
    │       │   └── Styles.xaml            # Fluent Design styles
    │       ├── Views/
    │       │   └── MainPage.xaml / .cs    # Translation UI (responsive layout)
    │       └── Assets/                    # App icons
    │
    └── e2e/
        ├── E2E.SidecarClient.csproj
        └── Program.cs                     # .NET E2E tests
```

---

## IPC Protocol Summary

**Request**: `{"id": "req-1", "method": "health", "params": {...}}`
**Response**: `{"id": "req-1", "result": {...}}` or `{"id": "req-1", "error": {"code": "...", "message": "..."}}`
**Event** (optional): `{"event": "translate_chunk", "id": "req-1", "data": {...}}`

**Supported methods**: `health`, `translate`, `shutdown`, `crash` (test only)

**Error codes**: `invalid_json`, `method_not_found`, `invalid_params`, `internal_error`

---

## Verification Commands (Windows)

### 1. Verify Python E2E (should already work)
```powershell
cd win32
python sidecar_mock/e2e_client.py
```

### 2. Build .NET SidecarClient
```powershell
cd win32/dotnet
dotnet build
```

### 3. Run .NET E2E tests
```powershell
cd win32/dotnet
dotnet run --project e2e/E2E.SidecarClient.csproj
```

---

## Milestone 1 Completed (C# Native Implementation)

Instead of Swift sidecar, we implemented C# native translation services:

1. ✅ **TranslationManager** - Service orchestration with caching and retry
2. ✅ **GoogleTranslateService** - Free Google Translate API (no key required)
3. ✅ **DeepLService** - DeepL API support (requires API key)
4. ✅ **Memory caching** - Avoids duplicate translation requests
5. ✅ **Exponential backoff retry** - Automatic retry on transient failures
6. ✅ **Language detection** - Auto-detect source language
7. ✅ **Responsive UI** - Adaptive layout for different window sizes

---

## Next Steps (Milestone 2: Native Integrations)

1. **System tray icon**:
   - Show app in system tray when minimized
   - Right-click context menu (Translate, Settings, Exit)
   - Double-click to show/hide window

2. **Global hotkeys**:
   - Register global hotkey (e.g., Ctrl+Alt+T) to show translation window
   - Hotkey to translate selected text
   - Configurable hotkey combinations

3. **Clipboard monitoring**:
   - Optional: auto-translate when text is copied
   - Toggle in settings

4. **Settings page**:
   - Configure hotkeys
   - Select default translation service
   - Enter API keys (DeepL, etc.)
   - Choose target language preference
   - Enable/disable clipboard monitoring

---

## SidecarClient API (for UI integration)

```csharp
// Create client
var client = new SidecarClient(new SidecarClientOptions
{
    ExecutablePath = "python",  // or path to Swift sidecar later
    Arguments = ["path/to/ipc_mock_service.py"],
    DefaultTimeoutMs = 30000
});

// Events
client.OnStderrLog += log => Debug.WriteLine(log);
client.OnProcessExited += code => ShowError("Sidecar exited");

// Start
client.Start();

// Send request
var response = await client.SendRequestAsync("translate", new {
    text = "hello",
    toLang = "zh"
});

if (response.IsSuccess)
{
    var result = response.Result.Value;
    var translated = result.GetProperty("translatedText").GetString();
}

// Stop
await client.StopAsync();
```

---

## E2E Test Coverage (Already Passing in WSL)

- ✅ Basic health request
- ✅ Basic translate request
- ✅ Unknown method returns error
- ✅ Concurrent requests (10 parallel, id-based multiplexing)
- ✅ Timeout handling (500ms timeout on 2s delayed request)
- ✅ Process crash detection (exit code 2)
- ✅ Graceful shutdown
- ✅ Stderr log collection

