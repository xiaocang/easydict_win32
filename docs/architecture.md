# Easydict Win32 Architecture Diagrams

## 1. Overall System Architecture

```mermaid
graph TB
    subgraph "Easydict.WinUI (Main Application)"
        App["App.xaml.cs<br/>Application Entry Point"]

        subgraph "Views"
            MainPage["MainPage<br/>Full Translation UI"]
            MiniWindow["MiniWindow<br/>Compact Floating Window"]
            FixedWindow["FixedWindow<br/>Always-on-Top Window"]
            PopButton["PopButtonWindow<br/>30×30 Selection Icon"]
        end

        subgraph "Application Services"
            TMS["TranslationManagerService<br/>Singleton Wrapper"]
            Settings["SettingsService<br/>JSON Persistence"]
            Localization["LocalizationService<br/>Multi-language i18n"]
            Clipboard["ClipboardService<br/>Clipboard Monitoring"]
            Hotkey["HotkeyService<br/>Global Hotkeys<br/>(RegisterHotKey)"]
            MouseHook["MouseHookService<br/>WH_MOUSE_LL +<br/>WH_KEYBOARD_LL"]
            PopButtonSvc["PopButtonService<br/>Pop Button Lifecycle"]
            TextSelection["TextSelectionService<br/>UI Automation +<br/>Ctrl+C Fallback"]
            MiniWindowSvc["MiniWindowService<br/>Singleton"]
            FixedWindowSvc["FixedWindowService<br/>Singleton"]
            TrayIcon["TrayIconService<br/>System Tray"]
        end
    end

    subgraph "Easydict.TranslationService (Library)"
        TM["TranslationManager<br/>Service Registry +<br/>HttpClient Pool + Cache"]

        subgraph "Translation Services"
            direction LR
            NonStream["Non-Streaming<br/>Google | Bing | DeepL<br/>Youdao | Linguee<br/>Caiyun | NiuTrans<br/>Volcano"]
            OpenAI["OpenAI-Compatible<br/>OpenAI | DeepSeek<br/>Groq | Zhipu<br/>GitHub Models<br/>Ollama | Custom"]
            CustomSSE["Custom SSE<br/>Gemini | Doubao"]
        end

        subgraph "Core Models"
            Request["TranslationRequest"]
            Result["TranslationResult"]
            Lang["Language Enum"]
        end

        subgraph "Streaming"
            SSE["SseParser"]
            Chat["ChatMessage"]
        end

        Security["SecretKeyManager<br/>AES-128 + DPAPI"]
    end

    subgraph "Easydict.SidecarClient (IPC)"
        Sidecar["SidecarClient<br/>JSON Lines over stdio"]
        Protocol["IPC Protocol<br/>Request/Response/Event"]
    end

    App --> TMS
    App --> Settings
    App --> Hotkey
    App --> MouseHook
    App --> PopButtonSvc
    App --> TrayIcon
    App --> Clipboard

    TMS --> TM
    TM --> NonStream
    TM --> OpenAI
    TM --> CustomSSE

    MainPage --> TMS
    MiniWindow --> TMS
    FixedWindow --> TMS

    PopButtonSvc --> TextSelection
    PopButtonSvc --> MiniWindowSvc
    MouseHook --> PopButtonSvc

    MiniWindowSvc --> MiniWindow
    FixedWindowSvc --> FixedWindow

    OpenAI --> SSE
    CustomSSE --> SSE
    NonStream --> Request
    OpenAI --> Request
    CustomSSE --> Request

    TM --> Security
    Settings --> Security
```

## 2. Translation Service Class Hierarchy

```mermaid
classDiagram
    class ITranslationService {
        <<interface>>
        +ServiceId: string
        +DisplayName: string
        +RequiresApiKey: bool
        +IsConfigured: bool
        +SupportedLanguages: IReadOnlyList~Language~
        +TranslateAsync(request, ct) TranslationResult
        +DetectLanguageAsync(text, ct) Language
    }

    class IStreamTranslationService {
        <<interface>>
        +IsStreaming: bool
        +TranslateStreamAsync(request, ct) IAsyncEnumerable~string~
    }

    class BaseTranslationService {
        <<abstract>>
        #HttpClient: HttpClient
        #ValidateRequest(request)
        #GetLanguageCode(lang) string
        #TranslateInternalAsync(request, ct)* TranslationResult
        +TranslateAsync(request, ct) TranslationResult
    }

    class BaseOpenAIService {
        <<abstract>>
        +Endpoint: string*
        +ApiKey: string*
        +Model: string*
        +Temperature: double
        #BuildSystemPrompt() string
        #BuildUserPrompt(request) string
        +TranslateStreamAsync(request, ct) IAsyncEnumerable~string~
    }

    ITranslationService <|-- IStreamTranslationService
    ITranslationService <|.. BaseTranslationService

    BaseTranslationService <|-- GoogleTranslateService
    BaseTranslationService <|-- GoogleWebTranslateService
    BaseTranslationService <|-- BingTranslateService
    BaseTranslationService <|-- DeepLService
    BaseTranslationService <|-- YoudaoService
    BaseTranslationService <|-- LingueeService
    BaseTranslationService <|-- CaiyunService
    BaseTranslationService <|-- NiuTransService
    BaseTranslationService <|-- VolcanoService

    BaseTranslationService <|-- BaseOpenAIService
    IStreamTranslationService <|.. BaseOpenAIService

    BaseOpenAIService <|-- OpenAIService
    BaseOpenAIService <|-- OllamaService
    BaseOpenAIService <|-- BuiltInAIService
    BaseOpenAIService <|-- DeepSeekService
    BaseOpenAIService <|-- GroqService
    BaseOpenAIService <|-- ZhipuService
    BaseOpenAIService <|-- GitHubModelsService
    BaseOpenAIService <|-- CustomOpenAIService

    BaseTranslationService <|-- GeminiService
    IStreamTranslationService <|.. GeminiService

    BaseTranslationService <|-- DoubaoService
    IStreamTranslationService <|.. DoubaoService
```

## 3. Translation Request Flow

```mermaid
sequenceDiagram
    actor User
    participant UI as MainPage / MiniWindow
    participant TMS as TranslationManagerService
    participant TM as TranslationManager
    participant Service as Translation Service
    participant SSE as SseParser
    participant Cache as MemoryCache

    User->>UI: Enter text / paste / clipboard
    UI->>UI: Debounce input

    alt Source Language = Auto
        UI->>TMS: AcquireHandle()
        TMS-->>UI: SafeManagerHandle
        UI->>Service: DetectLanguageAsync(text)
        Service-->>UI: Detected Language
    end

    UI->>UI: Build TranslationRequest<br/>(text, fromLang, toLang)

    loop For each enabled service
        UI->>TMS: AcquireHandle()
        TMS-->>UI: SafeManagerHandle

        UI->>TM: Get service by ID
        TM-->>UI: ITranslationService

        alt Cached result exists
            TM->>Cache: Lookup(request hash)
            Cache-->>TM: Cached TranslationResult
            TM-->>UI: Return cached result
        else Non-streaming service
            UI->>Service: TranslateAsync(request, ct)
            Service->>Service: ValidateRequest()
            Service->>Service: Start Stopwatch
            Service->>Service: TranslateInternalAsync()
            Service-->>UI: TranslationResult
        else Streaming service (OpenAI-compatible)
            UI->>Service: TranslateStreamAsync(request, ct)
            Service->>Service: Build system + user prompt
            Service->>Service: POST to API endpoint
            Service->>SSE: ParseStreamAsync(response)
            loop SSE chunks until [DONE]
                SSE-->>Service: content chunk
                Service-->>UI: yield chunk
                UI->>UI: Append to StreamingText
                UI->>UI: Throttle UI update (100ms)
            end
        end

        UI->>UI: Update ServiceQueryResult<br/>(IsLoading=false)
        UI->>UI: DispatcherQueue.TryEnqueue<br/>→ render result
    end

    UI-->>User: Display translation results
```

## 4. Mouse Selection Translate (Pop Button) Flow

```mermaid
sequenceDiagram
    actor User
    participant App as Source Application
    participant MH as MouseHookService<br/>(WH_MOUSE_LL)
    participant DD as DragDetector /<br/>MultiClickDetector
    participant PBS as PopButtonService
    participant TS as TextSelectionService
    participant PBW as PopButtonWindow
    participant MWS as MiniWindowService
    participant MW as MiniWindow

    Note over User,MW: Text Selection Detection

    alt Drag Selection
        User->>App: Mouse Down
        MH->>DD: Track press position
        User->>App: Mouse Move (>10px)
        MH->>DD: Set isDragging = true
        User->>App: Mouse Up
        MH->>DD: Drag ended
        DD->>PBS: OnDragSelectionEnd(cursorPos)
    else Double/Triple Click
        User->>App: Click × 2 or × 3
        MH->>DD: Track click count,<br/>timing, distance
        DD->>DD: Wait for possible<br/>next click
        DD->>PBS: OnMultiClickSelectionEnd(cursorPos)
    end

    Note over User,MW: Pop Button Display

    PBS->>PBS: Check IsEnabled<br/>(Settings.MouseSelectionTranslate)
    PBS->>PBS: Wait 150ms<br/>(let app finalize selection)
    PBS->>TS: GetSelectedTextAsync()

    alt UI Automation available
        TS->>App: FocusedElement.GetText()<br/>(FlaUI UIA3)
        App-->>TS: Selected text
    else Fallback to Ctrl+C
        TS->>TS: Save clipboard
        TS->>App: SendInput(Ctrl+C)<br/>(marked EASYDICT_SYNTHETIC_KEY)
        App-->>TS: Text via clipboard
        TS->>TS: Restore clipboard
    end

    TS-->>PBS: Selected text

    alt Text is non-empty
        PBS->>PBW: ShowAt(x, y)
        PBW->>PBW: SetWindowPos<br/>(TOPMOST, NOACTIVATE)
        PBW-->>User: Show 30×30 icon at cursor

        PBS->>PBS: Start auto-dismiss timer (5s)

        alt User clicks pop button
            User->>PBW: Click
            PBW->>PBS: OnPopButtonClicked()
            PBS->>MWS: ShowWithText(text)
            MWS->>MW: Show + set input text
            MW->>MW: StartQueryAsync(text)
            MW-->>User: Translation results
        else Auto-dismiss / other dismiss trigger
            Note over PBS: Triggers: click elsewhere,<br/>right-click, scroll,<br/>keyboard, timeout
            MH->>PBS: OnDismissTrigger()
            PBS->>PBW: Hide()
        end
    end
```

## 5. Application Initialization Flow

```mermaid
flowchart TD
    Start["App Constructor"] --> Init["InitializeComponent()"]
    Init --> UnhandledEx["Subscribe UnhandledException<br/>→ crash.log"]

    UnhandledEx --> OnLaunched["OnLaunched()"]
    OnLaunched --> CreateWindow["Create Main Window"]
    CreateWindow --> SetIcon["WindowIconService<br/>Set window icon"]
    SetIcon --> Navigate["Navigate to MainPage"]
    Navigate --> InitServices["InitializeServices()"]

    InitServices --> Tray["TrayIconService<br/>System tray icon"]
    InitServices --> HK["HotkeyService<br/>Register global hotkeys<br/>(Ctrl+Alt+T/D/M/F)"]
    InitServices --> CB["ClipboardService<br/>Clipboard monitoring"]
    InitServices --> MHS["MouseHookService<br/>Install WH_MOUSE_LL +<br/>WH_KEYBOARD_LL"]
    InitServices --> PBS2["PopButtonService<br/>Wire events:<br/>OnDragSelectionEnd<br/>OnDismissTrigger"]

    HK --> WireHotkeys["Wire hotkey handlers:<br/>OnShowWindow<br/>OnTranslateSelection<br/>OnShowMiniWindow<br/>OnShowFixedWindow<br/>OnToggleMiniWindow<br/>OnToggleFixedWindow"]

    InitServices --> AsyncInit["Async: Region Detection"]
    AsyncInit --> RegionCheck{"Is China region?"}
    RegionCheck -->|Yes| SetBing["Default service: Bing"]
    RegionCheck -->|No| SetGoogle["Default service: Google"]

    style Start fill:#e1f5fe
    style InitServices fill:#fff3e0
    style AsyncInit fill:#f3e5f5
```

## 6. Window Management Architecture

```mermaid
graph TB
    subgraph "Window Types"
        MainWin["MainWindow<br/>━━━━━━━━━━━━━<br/>Primary window<br/>Full translation UI<br/>System tray minimize<br/>Normal window behavior"]

        MiniWin["MiniWindow<br/>━━━━━━━━━━━━━<br/>Compact floating<br/>Pin toggle (always-on-top)<br/>Auto-close on focus loss<br/>Auto-resize by content"]

        FixedWin["FixedWindow<br/>━━━━━━━━━━━━━<br/>Always on top<br/>Persists on focus loss<br/>Same UI as MiniWindow"]

        PopBtnWin["PopButtonWindow<br/>━━━━━━━━━━━━━<br/>30×30 icon<br/>WS_EX_NOACTIVATE<br/>WS_EX_TOOLWINDOW<br/>WS_EX_TOPMOST"]
    end

    subgraph "Window Services (Singletons)"
        MiniSvc["MiniWindowService<br/>Show() / Hide() / Toggle()<br/>ShowWithText(text)"]
        FixedSvc["FixedWindowService<br/>Show() / Hide() / Toggle()<br/>ShowWithText(text)"]
        PopBtnSvc["PopButtonService<br/>OnDragSelectionEnd()<br/>Dismiss() / OnClicked()"]
    end

    subgraph "Activation Triggers"
        HK2["Global Hotkeys"]
        Mouse["Mouse Selection"]
        Tray2["System Tray"]
        Clip["Clipboard Change"]
    end

    HK2 -->|"Ctrl+Alt+T"| MainWin
    HK2 -->|"Ctrl+Alt+M"| MiniSvc
    HK2 -->|"Ctrl+Alt+F"| FixedSvc
    HK2 -->|"Ctrl+Alt+D"| MiniSvc

    Mouse --> PopBtnSvc
    PopBtnSvc --> PopBtnWin
    PopBtnSvc -->|"User clicks"| MiniSvc

    Tray2 --> MainWin
    Clip --> MainWin

    MiniSvc --> MiniWin
    FixedSvc --> FixedWin
```

## 7. IPC / Sidecar Architecture

```mermaid
sequenceDiagram
    participant App as Easydict.WinUI
    participant Client as SidecarClient
    participant Process as Sidecar Process<br/>(External)

    App->>Client: new SidecarClient(executablePath)
    Client->>Process: ProcessStartInfo<br/>RedirectStdio = true

    Note over Client,Process: JSON Lines Protocol<br/>(one JSON object per line)

    par Background Read Loops
        Client->>Client: ReadStdoutLoop()
        Client->>Client: ReadStderrLoop()
    end

    App->>Client: SendRequestAsync(method, params)
    Client->>Client: Generate unique ID
    Client->>Client: Register TaskCompletionSource[ID]
    Client->>Process: stdin: {"id":"abc","method":"ocr","params":{...}}\n

    Process-->>Client: stdout: {"id":"abc","result":{...}}\n
    Client->>Client: Match ID → complete TCS
    Client-->>App: IpcResponse

    Note over Client,Process: Events (no ID, fire-and-forget)
    Process-->>Client: stdout: {"event":"progress","data":{...}}\n
    Client->>App: OnEvent("progress", data)

    Process-->>Client: stderr: log message\n
    Client->>App: OnStderrLog(message)
```

## 8. Data Model Relationships

```mermaid
classDiagram
    class TranslationRequest {
        +Text: string
        +FromLanguage: Language
        +ToLanguage: Language
        +TimeoutMs: int = 30000
        +BypassCache: bool
    }

    class TranslationResult {
        +TranslatedText: string
        +OriginalText: string
        +DetectedLanguage: Language
        +TargetLanguage: Language
        +ServiceName: string
        +ServiceId: string
        +TimingMs: long
        +FromCache: bool
        +Alternatives: List~string~
        +WordResult: WordResult
    }

    class WordResult {
        +Phonetics: List~Phonetic~
        +Definitions: List~Definition~
        +Examples: List~string~
    }

    class ServiceQueryResult {
        +ServiceId: string
        +ServiceDisplayName: string
        +Result: TranslationResult
        +Error: string
        +IsLoading: bool
        +IsStreaming: bool
        +StreamingText: string
        +IsExpanded: bool
        +ManuallyToggled: bool
        +INotifyPropertyChanged
    }

    class Language {
        <<enumeration>>
        Auto
        SimplifiedChinese
        TraditionalChinese
        English
        Japanese
        Korean
        French
        German
        ...50+ languages
    }

    class TranslationManager {
        +Services: IReadOnlyDictionary
        +DefaultServiceId: string
        -_httpClientPool: HttpClient[]
        -_cache: MemoryCache
        -_phoneticCache: MemoryCache
        +ConfigureService(id, action)
    }

    class TranslationManagerService {
        +Instance: TranslationManagerService
        +Manager: TranslationManager
        +AcquireHandle() SafeManagerHandle
        -_handleCounts: Dictionary
    }

    class SafeManagerHandle {
        +Manager: TranslationManager
        +Dispose()
    }

    TranslationRequest --> Language
    TranslationResult --> Language
    TranslationResult --> WordResult
    ServiceQueryResult --> TranslationResult
    TranslationManager --> TranslationRequest
    TranslationManager --> TranslationResult
    TranslationManagerService --> TranslationManager
    TranslationManagerService --> SafeManagerHandle
    SafeManagerHandle --> TranslationManager
```

## 9. Settings & Configuration Flow

```mermaid
graph LR
    subgraph "SettingsService (Singleton)"
        JSON["settings.json<br/>AppData/Local/Easydict/"]
        Props["Properties:<br/>━━━━━━━━━━━<br/>API Keys (encrypted)<br/>Hotkey bindings<br/>Enabled services<br/>UI preferences<br/>Proxy config<br/>Language settings<br/>Window positions"]
    end

    subgraph "Consumers"
        TMS2["TranslationManagerService<br/>→ API keys, proxy,<br/>  service config"]
        HK3["HotkeyService<br/>→ Hotkey bindings"]
        MW2["MiniWindow<br/>→ Enabled services,<br/>  window position"]
        PBS3["PopButtonService<br/>→ MouseSelectionTranslate"]
        CB2["ClipboardService<br/>→ ClipboardMonitoring"]
        Theme["ThemeService<br/>→ AppTheme"]
        Loc["LocalizationService<br/>→ UILanguage"]
    end

    subgraph "Security"
        DPAPI["DPAPI<br/>(Windows Data Protection)"]
        AES["SecretKeyManager<br/>AES-128 CBC"]
    end

    JSON <--> Props
    Props --> TMS2
    Props --> HK3
    Props --> MW2
    Props --> PBS3
    Props --> CB2
    Props --> Theme
    Props --> Loc

    Props -.->|"API key<br/>encrypt/decrypt"| DPAPI
    Props -.->|"Built-in secrets"| AES
```

## 10. Streaming Translation Detail

```mermaid
sequenceDiagram
    participant UI as MiniWindow / MainPage
    participant Service as BaseOpenAIService
    participant HTTP as HttpClient
    participant API as LLM API Endpoint
    participant Parser as SseParser

    UI->>Service: TranslateStreamAsync(request, ct)
    Service->>Service: Build messages:<br/>system: "translation expert..."<br/>user: "Translate to {lang}: {text}"

    Service->>HTTP: POST /v1/chat/completions<br/>stream: true<br/>Authorization: Bearer {key}

    HTTP->>API: HTTPS request
    API-->>HTTP: 200 OK, Transfer-Encoding: chunked

    HTTP-->>Service: HttpResponseMessage (stream)
    Service->>Parser: ParseStreamAsync(responseStream)

    loop SSE Events
        API-->>Parser: data: {"choices":[{"delta":{"content":"Hello"}}]}\n\n
        Parser-->>Service: "Hello"
        Service-->>UI: yield "Hello"

        UI->>UI: sb.Append("Hello")

        alt Throttle check (≥100ms since last update)
            UI->>UI: DispatcherQueue.TryEnqueue
            UI->>UI: Update StreamingText
            UI->>UI: Render partial result
        end
    end

    API-->>Parser: data: [DONE]\n\n
    Parser-->>Service: (enumeration complete)
    Service-->>UI: (async enumerable done)

    UI->>UI: Final UI update
    UI->>UI: IsStreaming = false
    UI->>UI: IsLoading = false
```
