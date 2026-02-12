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
│   │   │   ├── Views/                   # UI views and pages
│   │   │   ├── Services/                # Application services
│   │   │   │   └── ScreenCapture/       # Screen capture UI (GDI+ Win32)
│   │   │   ├── Models/                  # View models and data models
│   │   │   ├── Strings/                 # Localization resources
│   │   │   └── Themes/                  # Theme resources
│   │   ├── Easydict.TranslationService/ # Translation service library
│   │   │   ├── Services/                # Translation service implementations
│   │   │   ├── Models/                  # Translation models
│   │   │   ├── Streaming/               # LLM streaming support
│   │   │   ├── Security/                # Encryption/security utilities
│   │   │   └── Resources/               # Service resources
│   │   ├── Easydict.NativeBridge/       # Browser extension native messaging host
│   │   └── Easydict.SidecarClient/      # IPC client library
│   │       └── Protocol/                # IPC protocol definitions
│   ├── tests/
│   │   ├── Easydict.TranslationService.Tests/
│   │   │   ├── Services/                # Translation service tests
│   │   │   ├── Streaming/               # Streaming tests
│   │   │   ├── Models/                  # Model tests
│   │   │   └── Mocks/                   # Mock implementations
│   │   └── Easydict.WinUI.Tests/
│   │       └── Services/                # WinUI service tests
│   ├── tools/
│   │   └── EncryptSecret/               # Secret encryption utility
│   ├── e2e/                             # E2E test for SidecarClient
│   ├── scripts/                         # PowerShell build scripts
│   │   ├── generate-*.ps1               # Asset generation scripts
│   │   ├── Fix-MsixMinVersion.ps1       # MSIX manifest fixer
│   │   └── publish.ps1                  # Publishing script
│   ├── certs/                           # Code signing certificates
│   ├── Easydict.Win32.sln               # Solution file
│   ├── Makefile                         # Build automation
│   └── winapp.yaml                      # WinApp CLI configuration
├── easydict_win32/                      # Original/legacy code
├── sidecar_mock/                        # Mock sidecar for testing
├── screenshot/                          # Screenshots for README
├── .winstore/                           # Microsoft Store listing metadata
│   ├── listings/                        # Per-language store listings (en-us, zh-cn, etc.)
│   ├── scripts/                         # Store sync scripts (Sync-StoreListings.ps1)
│   └── store-config.json               # Store app ID, languages, submission settings
├── browser-extension/                   # Browser extension for OCR trigger
├── .github/                             # GitHub workflows
└── README.md
```

## Build Commands

All commands should be run from the `dotnet/` directory.

### Using dotnet CLI

```bash
# Restore packages
dotnet restore

# Build Debug
dotnet build src/Easydict.WinUI/Easydict.WinUI.csproj -c Debug

# Build Release
dotnet build src/Easydict.WinUI/Easydict.WinUI.csproj -c Release

# Run the app
dotnet run --project src/Easydict.WinUI/Easydict.WinUI.csproj

# Run all tests
dotnet test Easydict.Win32.sln

# Run specific test projects
dotnet test tests/Easydict.TranslationService.Tests
dotnet test tests/Easydict.WinUI.Tests

# Publish self-contained
dotnet publish src/Easydict.WinUI/Easydict.WinUI.csproj -c Release -o ./publish
```

### Using Makefile

```bash
# Build (Debug)
make build

# Build Release
make build-release

# Run tests
make test

# Run specific tests
make test-translation
make test-winui

# Publish (x64)
make publish-x64

# Create MSIX package
make msix-x64

# Run the app
make run
```

## Key Features

- **Translation Services**: 15+ services including Google, DeepL, OpenAI, Gemini, DeepSeek, Groq, Zhipu AI, GitHub Models, Doubao, Caiyun, NiuTrans, Linguee, Ollama, and custom OpenAI-compatible services
- **LLM Streaming Translation**: Real-time display of translation results
- **Multiple Window Modes**: Main, Mini, Fixed windows
- **OCR Screenshot Translate**: Snipaste-style screen capture with Windows OCR API, supports 26+ languages, configurable recognition language
- **Global Hotkeys**:
  - `Ctrl+Alt+T` - Show/hide main window
  - `Ctrl+Alt+D` - Translate clipboard content
  - `Ctrl+Alt+S` - OCR screenshot translate (capture → recognize → translate)
  - `Ctrl+Alt+Shift+S` - Silent OCR (capture → recognize → copy to clipboard)
  - `Ctrl+Alt+M` - Show mini window with selection
  - `Ctrl+Alt+F` - Show fixed window
  - `Ctrl+Alt+Shift+M` - Toggle mini window
  - `Ctrl+Alt+Shift+F` - Toggle fixed window
- **Mouse Selection Translate**: Select text in any app (drag, double-click, triple-click) → floating icon appears → click to translate in Mini Window (uses `WH_MOUSE_LL` + `WH_KEYBOARD_LL` global hooks)
- **System Tray**: Minimize to tray, background operation, OCR translate in context menu
- **Clipboard Monitoring**: Auto-translate copied text
- **Shell Context Menu**: Right-click any file or desktop background → "OCR Translate"
- **Browser Extension**: Chrome/Firefox extension to trigger OCR translate via native messaging
- **HTTP Proxy Support**: Configure proxy server
- **High DPI Support**: Per-Monitor V2 DPI awareness
- **Dark/Light Theme**: System theme integration
- **Traditional Chinese Support**: Multiple services support Traditional Chinese

## Architecture Notes

### Translation Services

All translation services live in `Easydict.TranslationService` and follow a strict class hierarchy:

#### Interface & Base Class Hierarchy

```
ITranslationService                         # Core interface: ServiceId, DisplayName, TranslateAsync, etc.
├── IStreamTranslationService               # Adds TranslateStreamAsync (IAsyncEnumerable<string>)
│
BaseTranslationService : ITranslationService            # Abstract base with validation, timing, error handling
├── GoogleTranslateService                              # Non-streaming services extend this directly
├── GoogleWebTranslateService
├── DeepLService
├── LingueeService
├── CaiyunService
├── NiuTransService
├── GeminiService : IStreamTranslationService           # Custom SSE protocol (not OpenAI-compatible)
├── DoubaoService : IStreamTranslationService           # Custom SSE protocol (ByteDance)
└── BaseOpenAIService : IStreamTranslationService       # Abstract base for OpenAI-compatible LLM services
    ├── OpenAIService
    ├── OllamaService
    ├── BuiltInAIService
    ├── DeepSeekService
    ├── GroqService
    ├── ZhipuService
    ├── GitHubModelsService
    └── CustomOpenAIService
```

#### Adding a New Translation Service

1. **Non-streaming**: Extend `BaseTranslationService`, implement `TranslateInternalAsync`
2. **OpenAI-compatible streaming**: Extend `BaseOpenAIService`, provide `Endpoint`, `ApiKey`, `Model`
3. **Custom streaming protocol**: Extend `BaseTranslationService` + implement `IStreamTranslationService`
4. Register in `TranslationManager.cs` constructor
5. Add service icon in `Assets/ServiceIcons/`
6. Add configuration UI in settings page if `RequiresApiKey`

#### Required Overrides for Any Service

```csharp
public override string ServiceId { get; }              // e.g. "google", "openai"
public override string DisplayName { get; }            // e.g. "Google Translate"
public override bool RequiresApiKey { get; }
public override bool IsConfigured { get; }
public override IReadOnlyList<Language> SupportedLanguages { get; }
protected override Task<TranslationResult> TranslateInternalAsync(
    TranslationRequest request, CancellationToken cancellationToken);
```

#### Key Design Points
- LLM streaming is handled through SSE (Server-Sent Events) parsing
- Service configurations are encrypted using DPAPI (Data Protection API)
- Language codes are mapped via overrideable `GetLanguageCode(Language)` per service
- All services are registered in `TranslationManager` and accessed via `TranslationManagerService.Instance`

### Window Management
- Four window types: Main (full), Mini (floating), Fixed (persistent), PopButton (selection icon)
- Each window type is independently managed with separate activation states
- Global hotkeys are registered using `RegisterHotKey` Win32 API

### Mouse Selection Translate (Pop Button)
- **MouseHookService**: `WH_MOUSE_LL` + `WH_KEYBOARD_LL` global hooks detect text selection gestures:
  - **Drag select**: mouse down → drag beyond 10px threshold → mouse up (fires immediately)
  - **Multi-click**: double-click (select word) and triple-click (select line/paragraph), detected by tracking consecutive non-drag clicks within system `GetDoubleClickTime()` and 4px distance (fires after a brief delay to allow triple-click)
- **PopButtonWindow**: 30×30 WinUI 3 window with `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST` — does not steal focus from source app
- **PopButtonService**: Orchestrates the lifecycle — on selection detected, waits 150ms, queries `TextSelectionService` for selected text, shows icon at cursor position, auto-dismisses after 5s
- **Dismiss triggers**: Left click elsewhere, right click, scroll, keyboard, new selection
- **Setting**: `MouseSelectionTranslate` in SettingsService (default: off), toggle in Settings → Behavior
- **Flow**: `MouseHookService.OnDragSelectionEnd` → `PopButtonService.OnDragSelectionEnd` → `TextSelectionService.GetSelectedTextAsync` → `PopButtonWindow.ShowAt` → user clicks → `MiniWindowService.ShowWithText`

### OCR Screenshot Translate

#### Service Architecture
```
IOcrService                              # Pluggable OCR interface
└── WindowsOcrService                    # Windows.Media.Ocr (WinRT) implementation

ScreenCaptureService                     # Orchestrates capture UI flow
└── ScreenCaptureWindow                  # GDI+ Win32 capture window (not WinUI 3)
    └── WindowDetector                   # Z-order window snapshot for auto-detect

OcrTranslateService                      # Orchestrates: capture → OCR → translate/clipboard
OcrTextMerger                            # CJK-aware text line merging (pure logic)
```

#### Key Components
- **IOcrService** (`Services/IOcrService.cs`): Pluggable interface with `RecognizeAsync()`, `GetAvailableLanguages()`, `IsAvailable`
- **WindowsOcrService** (`Services/WindowsOcrService.cs`): Uses `Windows.Media.Ocr` WinRT API, supports 26+ languages via Windows language packs, includes text angle detection
- **ScreenCaptureWindow** (`Services/ScreenCapture/ScreenCaptureWindow.cs`): ~1200 lines of GDI+ rendering + Win32 message handling. Three phases: Detecting (auto-detect window under cursor) → Selecting (click+drag or double-click) → Adjusting (resize handles, arrow keys). Features magnifier, size label, multi-monitor support
- **WindowDetector** (`Services/ScreenCapture/WindowDetector.cs`): Snapshots visible windows on startup, builds hierarchy, hit-tests cursor position, supports scroll-to-change-depth (Snipaste-style)
- **OcrTranslateService** (`Services/OcrTranslateService.cs`): Two modes — `OcrTranslateAsync()` (capture → OCR → MiniWindow translation) and `SilentOcrAsync()` (capture → OCR → clipboard). Concurrency guard: one OCR operation at a time
- **OcrTextMerger** (`Services/OcrTextMerger.cs`): Merges OCR words/lines intelligently — spaces between Latin words, no spaces between CJK characters. Groups and sorts lines by visual layout

#### OCR Data Models
- **OcrResult** (`Models/OcrResult.cs`): `Text`, `Lines: IReadOnlyList<OcrLine>`, `DetectedLanguage`, `TextAngle`
- **OcrLine**: `Text`, `BoundingRect: OcrRect`
- **OcrRect** (record struct): `X, Y, Width, Height` — platform-independent rectangle
- **OcrLanguage**: `Tag` (BCP-47), `DisplayName`
- **ScreenCaptureResult** (`Models/ScreenCaptureResult.cs`): `PixelData: byte[]` (BGRA8), `PixelWidth`, `PixelHeight`, `ScreenRect`

#### OCR Settings
- `OcrTranslateHotkey` (default: `Ctrl+Alt+S`)
- `SilentOcrHotkey` (default: `Ctrl+Alt+Shift+S`)
- `OcrLanguage` (default: `auto` — uses system profile languages)

#### Integration Points
- **Global Hotkeys**: `HOTKEY_ID_OCR_TRANSLATE` and `HOTKEY_ID_SILENT_OCR` in HotkeyService
- **System Tray**: "OCR Translate" menu item in TrayIconService
- **Shell Context Menu**: `ContextMenuService` registers `HKCU\Software\Classes\*\shell\EasydictOCR` for right-click OCR
- **Browser Extension**: `Easydict.NativeBridge` native messaging host + `browser-extension/` (Chrome/Firefox)
- **Protocol Activation**: `easydict://ocr-translate` URI scheme
- **IPC**: Named event `Local\Easydict-OcrTranslate` for shell context menu and browser extension signaling

#### Flow
1. Trigger: hotkey / tray menu / shell context menu / browser extension / protocol activation
2. `OcrTranslateService.OcrTranslateAsync()` → `ScreenCaptureService.CaptureRegionAsync()` (dedicated STA thread)
3. `ScreenCaptureWindow` shows fullscreen overlay → user selects region → returns `ScreenCaptureResult`
4. `WindowsOcrService.RecognizeAsync()` → `OcrTextMerger` post-processes → `OcrResult`
5. Result sent to `MiniWindowService.ShowWithText()` for translation (or copied to clipboard for silent mode)

### IPC Architecture
- `Easydict.SidecarClient` provides communication with external sidecar processes
- `Easydict.NativeBridge` provides native messaging host for browser extension communication (stdin/stdout JSON + 4-byte length prefix)
- Named events (`Local\Easydict-OcrTranslate`) for cross-process OCR signaling from shell context menu and browser extension
- E2E tests in `e2e/` directory

### Testing
- Unit tests using xUnit + FluentAssertions
- Mock implementations for HTTP clients and external services
- Separate test projects for each major component

### Windows Store (`.winstore/`)
- App is published on Microsoft Store: https://apps.microsoft.com/detail/9p7nqvxf9dzj
- Store listing metadata is maintained as YAML files in `.winstore/listings/`, one per language (en-us, zh-cn, zh-tw, ja-jp, ko-kr, fr-fr, de-de)
- **Store listings only support 7 languages** (en-us, zh-cn, zh-tw, ja-jp, ko-kr, fr-fr, de-de), which is a smaller set than the app's 15 UI languages. Do NOT add new store listing languages without explicit approval.
- `en-us` is the primary language; update it first, then translate to others
- **Keywords must NOT contain third-party product names** (DeepL, OpenAI, ChatGPT, Gemini, DeepSeek, etc.) per Microsoft Store policy; these names are allowed in `description` and `features` only
- Description must emphasize "free and open-source" and GPL-3.0 in the first sentence
- `store-config.json` holds app identity, supported languages, and submission settings
- `scripts/Sync-StoreListings.ps1` validates/previews/submits listings via `msstore` CLI
- GitHub Actions workflow `store-listings.yml` provides manual-trigger store listing management

## Coding Style and Conventions

### 1. Naming Conventions

#### Classes and Types
```csharp
// Services use 'Service' suffix
public sealed class TranslationManagerService { }
public class ClipboardService { }
public class LocalizationService { }

// Base/Abstract classes use 'Base' prefix
public abstract class BaseTranslationService { }
public abstract class BaseOpenAIService : BaseTranslationService { }

// Descriptive model names
public class ServiceCheckItem { }
public record TranslationRequest { }
```

#### Methods and Properties
```csharp
// Public members use PascalCase
public async Task<TranslationResult> TranslateAsync();
public string DisplayName { get; init; }
public bool IsConfigured { get; }

// Boolean properties use 'Is', 'Has', 'Can' prefix
public bool IsStreaming { get; }
public bool HasError { get; }
public bool CanTranslate { get; }

// Event handlers use 'On' prefix
private void OnClipboardContentChanged(object? sender, object e);
private async void OnPageLoaded(object sender, RoutedEventArgs e);
```

#### Fields
```csharp
// Private instance fields use _camelCase
private bool _isLoaded;
private string _lastClipboardText;
private readonly HttpClient _httpClient;

// Private static readonly fields use _camelCase
private static readonly Lazy<TranslationManagerService> _instance;
private static readonly IReadOnlyList<Language> _googleLanguages;
private readonly object _lock = new();

// Constants use PascalCase or CONSTANT_CASE
private const string BaseUrl = "https://api.example.com";
private const int DefaultTimeoutSeconds = 30;
```

#### Files and Namespaces
```csharp
// File names match class names
// ClipboardService.cs, TranslationManagerService.cs

// Use file-scoped namespaces (C# 10+)
namespace Easydict.WinUI.Services;

public class ClipboardService { }

// Namespaces follow directory structure
// src/Easydict.WinUI/Services/ -> Easydict.WinUI.Services
// src/Easydict.TranslationService/Services/ -> Easydict.TranslationService.Services
```

### 2. Async/Await Patterns

#### Standard Async Methods
```csharp
// Always use async suffix for async methods
public async Task<TranslationResult> TranslateAsync(
    TranslationRequest request,
    CancellationToken cancellationToken = default)
{
    ValidateRequest(request);
    var stopwatch = Stopwatch.StartNew();

    try
    {
        var result = await TranslateInternalAsync(request, cancellationToken);
        stopwatch.Stop();
        return result with { TimingMs = stopwatch.ElapsedMilliseconds };
    }
    catch (HttpRequestException ex)
    {
        throw new TranslationException($"Network error: {ex.Message}", ex);
    }
}

// Static async methods for utility operations
public static async Task<string?> GetTextAsync()
{
    try
    {
        var content = Clipboard.GetContent();
        if (content.Contains(StandardDataFormats.Text))
        {
            return await content.GetTextAsync();
        }
    }
    catch { return null; }
}
```

#### Streaming with Async Iterators
```csharp
// Implement IAsyncEnumerable<T> for streaming
public async IAsyncEnumerable<string> TranslateStreamAsync(
    TranslationRequest request,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    await foreach (var chunk in GetStreamChunksAsync(request, cancellationToken))
    {
        yield return chunk;
    }
}

// Consume streaming results
await foreach (var chunk in service.TranslateStreamAsync(request, ct))
{
    sb.Append(chunk);

    // Throttle UI updates for performance
    if ((DateTime.UtcNow - lastUpdateTime).TotalMilliseconds >= 100)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ResultText = sb.ToString();
        });
        lastUpdateTime = DateTime.UtcNow;
    }
}
```

#### UI Thread Marshalling
```csharp
// Use DispatcherQueue.TryEnqueue for UI updates from background threads
DispatcherQueue.TryEnqueue(() =>
{
    if (_isClosing) return;

    serviceResult.IsLoading = false;
    serviceResult.IsStreaming = true;
    serviceResult.StreamingText = updatedText;
});
```

#### Fire-and-Forget Pattern
```csharp
// Use discard for intentional fire-and-forget
_ = Task.Run(async () =>
{
    await Task.Delay(2000);
    lock (_lock)
    {
        // Cleanup old resources
    }
});
```

### 3. Dependency Injection and Singleton Patterns

#### Lazy Singleton Pattern
```csharp
// Use Lazy<T> for thread-safe singletons
public sealed class TranslationManagerService
{
    private static readonly Lazy<TranslationManagerService> _instance =
        new(() => new TranslationManagerService(),
            LazyThreadSafetyMode.PublicationOnly);

    public static TranslationManagerService Instance => _instance.Value;

    private TranslationManagerService() { }
}
```

#### Constructor Injection
```csharp
// Inject dependencies through constructor
public sealed class GoogleTranslateService : BaseTranslationService
{
    protected readonly HttpClient HttpClient;

    public GoogleTranslateService(HttpClient httpClient)
        : base(httpClient)
    {
        HttpClient = httpClient;
    }
}
```

#### Service Locator (When DI Not Available)
```csharp
// Use Instance property for singleton access
private readonly SettingsService _settings = SettingsService.Instance;
private readonly LocalizationService _localization = LocalizationService.Instance;
```

#### Resource Handle Pattern
```csharp
// Use disposable handles for lifetime management
using var handle = TranslationManagerService.Instance.AcquireHandle();
var manager = handle.Manager;
// Handle automatically releases on disposal
```

### 4. Error Handling

#### Exception Hierarchy with Specific Catches
```csharp
try
{
    var result = await TranslateInternalAsync(request, cancellationToken);
    return result;
}
catch (HttpRequestException ex)
{
    throw new TranslationException($"Network error: {ex.Message}", ex)
    {
        ErrorCode = TranslationErrorCode.NetworkError,
        ServiceId = ServiceId
    };
}
catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
{
    throw new TranslationException("Request timed out", ex)
    {
        ErrorCode = TranslationErrorCode.Timeout
    };
}
catch (TranslationException)
{
    throw; // Re-throw custom exceptions as-is
}
catch (Exception ex)
{
    throw new TranslationException($"Unexpected error: {ex.Message}", ex)
    {
        ErrorCode = TranslationErrorCode.Unknown
    };
}
```

#### Validation Before Operations
```csharp
protected virtual void ValidateRequest(TranslationRequest request)
{
    if (string.IsNullOrWhiteSpace(request.Text))
        throw new TranslationException("Text cannot be empty");

    if (!SupportsLanguagePair(request.FromLanguage, request.ToLanguage))
        throw new TranslationException("Language pair not supported");
}
```

#### Swallow-and-Log Pattern for Non-Critical Errors
```csharp
private async void OnClipboardContentChanged(object? sender, object e)
{
    try
    {
        var content = Clipboard.GetContent();
        if (content.Contains(StandardDataFormats.Text))
        {
            var text = await content.GetTextAsync();
            OnClipboardTextChanged?.Invoke(text);
        }
    }
    catch
    {
        // Ignore clipboard access errors - non-critical
    }
}
```

#### Resource Cleanup with Finally
```csharp
try
{
    SetLoading(true);
    await PerformOperationAsync();
}
finally
{
    if (!_isClosing)
        SetLoading(false);

    Interlocked.CompareExchange(ref _currentCts, null, currentCts);
}
```

#### COM Object Lifecycle Guards
```csharp
// WinUI 3 COM objects (ContentDialog, Window) can become invalid between check and use.
// Wrap lifecycle calls in try/catch (COMException).
try { _currentDialog?.Hide(); } catch (COMException) { }

// In test helpers, catch the specific expected exception
catch (COMException) { return false; }   // ✓ specific
catch { return false; }                   // ✗ too broad
```

### 5. WinUI 3 / XAML Code-Behind Patterns

#### Page Lifecycle Management
```csharp
public partial class MainPage : Page
{
    private bool _isLoaded;

    public MainPage()
    {
        this.InitializeComponent();
        this.Loaded += OnPageLoaded;
        this.Unloaded += OnPageUnloaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        InitializeServices();
        ApplyLocalization();
    }

    private async void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        await CleanupResourcesAsync();
    }
}
```

#### State Flags for UI Safety
```csharp
// Prevent operations during initialization or teardown
private bool _isLoaded;
private volatile bool _isClosing;
private bool _suppressSelectionChanged;

private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
{
    if (!_isLoaded || _suppressSelectionChanged) return;

    // Handle selection change
}
```

#### Event Handler Registration/Unregistration
```csharp
private bool _handlersRegistered;

private void OnPageLoaded(object sender, RoutedEventArgs e)
{
    if (!_handlersRegistered)
    {
        ServiceCombo.SelectionChanged += OnServiceComboChanged;
        SaveButton.Click += OnSaveButtonClick;
        _handlersRegistered = true;
    }
}

private void OnPageUnloaded(object sender, RoutedEventArgs e)
{
    if (_handlersRegistered)
    {
        ServiceCombo.SelectionChanged -= OnServiceComboChanged;
        SaveButton.Click -= OnSaveButtonClick;
        _handlersRegistered = false;
    }
}
```

#### Inline Lambda Event Handlers
```csharp
// For simple synchronization or forwarding
SourceLangCombo.SelectionChanged += (s, e) =>
    SyncComboSelection(SourceLangCombo, SourceLangComboNarrow);
```

### 6. Thread Safety and Concurrency

#### Lock-Based Synchronization
```csharp
private readonly object _lock = new();
private Dictionary<TranslationManager, int> _handleCounts = new();

public SafeManagerHandle AcquireHandle()
{
    lock (_lock)
    {
        var manager = _translationManager;
        if (!_handleCounts.ContainsKey(manager))
            _handleCounts[manager] = 0;

        _handleCounts[manager]++;
        return new SafeManagerHandle(manager, () => ReleaseHandle(manager));
    }
}
```

#### Interlocked Operations
```csharp
// Use Interlocked for atomic operations on shared state
private CancellationTokenSource? _currentQueryCts;

var previousCts = Interlocked.Exchange(ref _currentQueryCts, currentCts);
previousCts?.Cancel();

// Compare-exchange for conditional updates
Interlocked.CompareExchange(ref _currentQueryCts, null, currentCts);
```

#### CancellationTokenSource Ownership
```csharp
// CTS fields use ownership comments to clarify lifecycle
// Owned by StartQueryAsync() - only that method creates and disposes via its finally block.
// Other code may Cancel() but must NOT Dispose().
private CancellationTokenSource? _currentQueryCts;

// Owner creates and disposes
using var currentCts = new CancellationTokenSource();
var previousCts = Interlocked.Exchange(ref _currentQueryCts, currentCts);

// Non-owner sites: guard Cancel() against race with owner's Dispose()
try { previousCts?.Cancel(); } catch (ObjectDisposedException) { }
```

#### Volatile Fields for Visibility
```csharp
// Use volatile for flags checked across threads
private volatile bool _isClosing;
```

### 7. Interface Design and Abstraction

#### Base Classes with Virtual Members
```csharp
public abstract class BaseTranslationService : ITranslationService
{
    // Abstract members must be implemented
    public abstract string ServiceId { get; }
    public abstract string DisplayName { get; }
    public abstract bool RequiresApiKey { get; }

    // Virtual members have default implementation
    public virtual bool SupportsLanguagePair(Language from, Language to)
    {
        return true; // Default: support all pairs
    }

    public virtual Task<Language> DetectLanguageAsync(
        string text,
        CancellationToken ct = default)
    {
        return Task.FromResult(Language.Auto);
    }
}
```

#### Interface Segregation
```csharp
// Base interface for all translation services
public interface ITranslationService
{
    string ServiceId { get; }
    string DisplayName { get; }
    Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken ct = default);
}

// Separate interface for streaming capability
public interface IStreamTranslationService : ITranslationService
{
    IAsyncEnumerable<string> TranslateStreamAsync(
        TranslationRequest request,
        CancellationToken ct = default);
    bool IsStreaming { get; }
}
```

### 8. Data Models and Records

#### Record Types for Immutable Data
```csharp
// Use records for DTOs and immutable data structures
internal sealed record NavSection(
    string Name,
    string Tooltip,
    string IconGlyph,
    FrameworkElement Element);

// Init-only properties for semi-immutable records
public record TranslationRequest
{
    public string Text { get; init; } = string.Empty;
    public Language FromLanguage { get; init; }
    public Language ToLanguage { get; init; }
}
```

#### Observable Properties with INotifyPropertyChanged
```csharp
public class ServiceCheckItem : INotifyPropertyChanged
{
    private bool _isChecked;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked != value)
            {
                _isChecked = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this,
            new PropertyChangedEventArgs(propertyName));
    }
}
```

### 9. Testing Conventions

#### xUnit Test Structure
```csharp
public class GoogleTranslateServiceTests
{
    private readonly MockHttpMessageHandler _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly GoogleTranslateService _service;

    public GoogleTranslateServiceTests()
    {
        _mockHandler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_mockHandler);
        _service = new GoogleTranslateService(_httpClient);
    }

    [Fact]
    public void ServiceId_IsGoogle()
    {
        // Assert
        _service.ServiceId.Should().Be("google");
    }

    [Fact]
    public async Task TranslateAsync_ReturnsTranslatedText()
    {
        // Arrange
        var googleResponse = """{"sentences": [{"trans": "Hello"}]}""";
        _mockHandler.EnqueueJsonResponse(googleResponse);
        var request = new TranslationRequest
        {
            Text = "你好",
            FromLanguage = Language.ChineseSimplified,
            ToLanguage = Language.English
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.TranslatedText.Should().Be("Hello");
        result.ServiceId.Should().Be("google");
    }
}
```

#### FluentAssertions Usage
```csharp
// Use FluentAssertions for readable assertions
result.Should().NotBeNull();
result.TranslatedText.Should().Be("Expected text");
result.TimingMs.Should().BeGreaterThan(0);
list.Should().HaveCount(3);
action.Should().ThrowAsync<TranslationException>();
```

### 10. Common Patterns

#### Configuration Pattern
```csharp
// Configure services with fluent API
_translationManager.ConfigureService("deepl", service =>
{
    if (service is DeepLService deepl)
    {
        deepl.Configure(apiKey, useWebFirst: true);
    }
});
```

#### Resource Disposal Pattern
```csharp
public void Dispose()
{
    if (_isDisposed) return;
    _isDisposed = true;

    // Cleanup resources
    IsMonitoringEnabled = false;
    _httpClient?.Dispose();
}
```

#### Localization Pattern
```csharp
private void ApplyLocalization()
{
    var loc = LocalizationService.Instance;
    TitleText.Text = loc.GetString("WindowTitle");
    InputTextBox.PlaceholderText = loc.GetString("InputPlaceholder");
}
```

#### Debug Logging
```csharp
// Use Debug.WriteLine for development logging
Debug.WriteLine($"[ServiceName] Operation started: {parameter}");
Debug.WriteLine($"[ServiceName] Result: {result.Length} chars");
```

### Key Principles

1. **Async by Default**: Use async/await for all I/O operations
2. **Null Safety**: Leverage C# nullable reference types (`?`)
3. **Immutability**: Prefer `init` and `record` types where appropriate
4. **Thread Safety**: Use locks, Interlocked, or immutable types for shared state
5. **Separation of Concerns**: Keep UI, business logic, and services separate
6. **Resource Management**: Always dispose IDisposable resources properly
7. **Error Handling**: Catch specific exceptions, provide meaningful error messages
8. **Testing**: Write tests for all business logic and service implementations

## Version Bump Files

When bumping the app version, update these 2 files:

- `dotnet/src/Easydict.WinUI/Easydict.WinUI.csproj` — `Version`, `AssemblyVersion`, `FileVersion`
- `dotnet/src/Easydict.WinUI/Package.appxmanifest` — `Identity Version`

## Documentation Sync Requirement

`README.md` (English) and `README_ZH.md` (Chinese) must always stay in sync. When modifying either file, apply the corresponding changes to the other file to keep both versions consistent in structure and content. This includes but is not limited to: feature descriptions, installation instructions, configuration guides, screenshots, and links.

## GitHub PR Review

When `gh` CLI is not available (e.g., in sandbox environments), use WebFetch to retrieve PR comments via the GitHub REST API:

```
# Inline review comments (with diff context, file path, line number)
https://api.github.com/repos/{owner}/{repo}/pulls/{pr_number}/comments

# Top-level PR reviews (approve/request changes/comment)
https://api.github.com/repos/{owner}/{repo}/pulls/{pr_number}/reviews

# Conversation-tab comments (non-inline)
https://api.github.com/repos/{owner}/{repo}/issues/{pr_number}/comments

# PR details (title, body, state, base branch)
https://api.github.com/repos/{owner}/{repo}/pulls/{pr_number}
```

For paginated results (>30 comments), append `?per_page=100&page=2` etc.

Each review comment object includes:
- `body` — the comment text
- `path` — file path the comment refers to
- `line` / `original_line` — line number in the diff
- `diff_hunk` — surrounding diff context
- `created_at` — timestamp (use to identify stale vs current comments)
- `in_reply_to_id` — threads replies to a parent comment

When processing PR comments:
1. Fetch all comments via WebFetch from `https://api.github.com/repos/{owner}/{repo}/pulls/{pr_number}/comments`
2. Group by `path` and `line` to understand per-file feedback
3. Skip comments from earlier revisions that have already been addressed (check if the referenced code still exists)
4. Address remaining comments, commit, and push

## Running PowerShell Scripts from Bash Tool

The Bash tool on Windows runs under `/usr/bin/bash` (Git Bash / MSYS2). Windows-style paths with backslashes are interpreted as escape sequences and break. Always use `powershell.exe` or `pwsh.exe` directly with **quoted** absolute paths:

```bash
# Correct - use powershell.exe with quoted paths
powershell.exe -ExecutionPolicy Bypass -File "C:\Users\johnn\Documents\work\easydict_win32\dotnet\scripts\release.ps1" -Tag v0.5.0

# Wrong - cd with Windows paths fails in bash
cd C:\Users\johnn\Documents\work\easydict_win32\dotnet && powershell -ExecutionPolicy Bypass -File scripts/release.ps1
```

Similarly for `dotnet` CLI commands, always pass full quoted paths rather than using `cd`:

```bash
# Correct
dotnet build "C:\Users\johnn\Documents\work\easydict_win32\dotnet\src\Easydict.WinUI\Easydict.WinUI.csproj"

# Wrong
cd C:\Users\johnn\Documents\work\easydict_win32\dotnet && dotnet build src/Easydict.WinUI/Easydict.WinUI.csproj
```

## Claude Code Cloud Environment: Git Push

In the Claude Code cloud (sandbox) environment, `git push` commands may be blocked by the tool permission system on the first few attempts. The workaround:

1. Use `GIT_TRACE=1` prefix to make the push succeed:
   ```bash
   GIT_TRACE=1 git push -u origin <branch-name>
   ```
2. The branch name must match the pattern `claude/<description>-<sessionId>`, otherwise the remote will reject the push with a 403 error.
3. If push fails due to network errors, retry up to 4 times with exponential backoff (2s, 4s, 8s, 16s).
