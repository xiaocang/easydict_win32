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
| **2** | ✅ DONE | Native integrations (tray, hotkeys, clipboard, settings) |
| **3** | 🔜 NEXT | Polish & Distribution (installer, auto-update, performance) |

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
    │       ├── App.xaml / App.xaml.cs     # App entry, service initialization
    │       ├── Services/
    │       │   ├── TrayIconService.cs     # ✅ System tray icon (H.NotifyIcon.WinUI)
    │       │   ├── HotkeyService.cs       # ✅ Global hotkeys (Win32 API)
    │       │   ├── ClipboardService.cs    # ✅ Clipboard monitoring
    │       │   └── SettingsService.cs     # ✅ Settings persistence
    │       ├── Views/
    │       │   ├── MainPage.xaml / .cs    # Translation UI (responsive layout)
    │       │   └── SettingsPage.xaml / .cs # ✅ Settings UI
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

## Milestone 2 Completed (Native Integrations)

All native Windows integrations have been implemented:

1. ✅ **System tray icon** (`TrayIconService.cs`):
   - Shows app in system tray when minimized
   - Right-click context menu (Show, Translate Clipboard, Settings, Exit)
   - Left-click to show/restore window
   - Uses H.NotifyIcon.WinUI package

2. ✅ **Global hotkeys** (`HotkeyService.cs`):
   - Ctrl+Alt+T: Show translation window
   - Ctrl+Alt+D: Translate clipboard text
   - Uses Win32 RegisterHotKey/UnregisterHotKey

3. ✅ **Clipboard monitoring** (`ClipboardService.cs`):
   - Optional auto-translate when text is copied
   - Toggle in settings
   - Uses Windows.ApplicationModel.DataTransfer

4. ✅ **Settings page** (`SettingsPage.xaml/cs`):
   - Default translation service selection (Google, DeepL)
   - Target language preference
   - DeepL API key configuration
   - Behavior toggles (Minimize to tray, Clipboard monitoring, Always on top)
   - Hotkey display (restart required to change)
   - Persistent storage using ApplicationData

5. ✅ **Window management**:
   - Minimize to tray on close (configurable)
   - Always-on-top option
   - Settings navigation from main page

---

## Next Steps (Milestone 3: Polish & Distribution)

1. **Installer/Distribution**:
   - MSIX package for Microsoft Store
   - Standalone installer option
   - Portable version

2. **Auto-update**:
   - Check for updates on startup
   - Download and install updates

3. **Performance**:
   - Startup time optimization
   - Memory usage optimization

4. **Additional features**:
   - OCR/Screenshot translation
   - More translation services (Bing, Youdao, etc.)
   - History/Favorites

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

