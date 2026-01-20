# AGENTS.md (easydict_win32)
Windows port of Easydict built with .NET 8 + WinUI 3.
Most work is under `dotnet/`.

Agent expectations:
- Keep diffs small and reviewable; match nearby patterns.
- Avoid sweeping refactors or repo-wide formatting unless asked.
- Prefer adding tests for bug fixes and behavior changes.

Related rule files:
- `.claude/CLAUDE.md` (project overview + basic commands).
- No Cursor rules found (`.cursor/rules/` or `.cursorrules`).
- No Copilot instructions found (`.github/copilot-instructions.md`).

## Repository Map
- `dotnet/Easydict.Win32.sln`: solution entry point.
- `dotnet/src/Easydict.WinUI/`: WinUI app (`net8.0-windows10.0.19041.0`).
- `dotnet/src/Easydict.TranslationService/`: translation library (`net8.0`).
- `dotnet/src/Easydict.SidecarClient/`: JSONL-over-stdio IPC client.
- `dotnet/tests/*`: xUnit tests (FluentAssertions).
- `sidecar_mock/`: mock sidecar for local testing.

Platform notes: WinUI projects build/test on Windows runners; `Easydict.TranslationService` + its tests are cross-platform.

## Build / Run
Run commands from `dotnet/` unless noted.

```bash
# Restore (CI does this first)
dotnet restore Easydict.Win32.sln

# Build (matches CI)
dotnet build Easydict.Win32.sln -c Debug --no-restore

# Build only the app
dotnet build src/Easydict.WinUI/Easydict.WinUI.csproj -c Debug

# Run the app (Windows)
dotnet run --project src/Easydict.WinUI/Easydict.WinUI.csproj

# Publish (release artifact)
dotnet publish src/Easydict.WinUI/Easydict.WinUI.csproj -c Release -o ./publish --self-contained false
```
Makefile targets (from `dotnet/`): `make build`, `make build-release`, `make test`, `make test-translation`, `make test-winui`, `make publish`.

## Lint / Formatting
No repo-wide formatter configuration is checked in (no `.editorconfig`).
Treat compiler + analyzers during `dotnet build` as the primary lint signal.

Optional checks (avoid mass reformatting):
- `dotnet build Easydict.Win32.sln -c Debug -warnaserror`
- `dotnet format Easydict.Win32.sln` (only if installed)

## Tests
```bash
# All tests (matches CI)
dotnet test Easydict.Win32.sln --no-build --verbosity normal

# Run a single test from the solution (handy on CI runners)
dotnet test Easydict.Win32.sln --filter "FullyQualifiedName~TranslationManagerTests" --no-build

# Single project
dotnet test tests/Easydict.TranslationService.Tests --logger "console;verbosity=minimal"
dotnet test tests/Easydict.WinUI.Tests --logger "console;verbosity=minimal"

# Single test (xUnit): discover + filter
dotnet test tests/Easydict.TranslationService.Tests --list-tests
dotnet test tests/Easydict.TranslationService.Tests --filter "FullyQualifiedName~TranslationManagerTests"
dotnet test tests/Easydict.TranslationService.Tests --filter "FullyQualifiedName~GoogleTranslateServiceTests.TranslateAsync_ReturnsTranslatedText"
```

Notes:
- Prefer filtering by `FullyQualifiedName~...` (more stable than display names).
- Keep tests hermetic: no real network calls; use mocks (e.g., `MockHttpMessageHandler`).

## Code Style (Match Existing Code)
This repo uses modern C# (nullable enabled, file-scoped namespaces, `required`/`init`, raw string literals in tests). Keep changes consistent.

General C# conventions:
- Files/namespaces: file-scoped namespaces; namespaces follow folders; one primary type per file.
- Imports: rely on implicit usings; WinUI globals in `dotnet/src/Easydict.WinUI/Imports.cs`; order `System.*` then `Easydict.*` then third-party.
- Formatting: 4 spaces; braces on new lines; prefer early returns; keep public members before private helpers.
- Language features: use `var` when the RHS makes the type obvious; prefer `sealed` for classes not meant for inheritance.
- Types/nullability: annotate `?` correctly; avoid `!`; prefer `IReadOnly*` for exposed collections.
- Naming: PascalCase public API; camelCase locals/params; `_camelCase` fields; async ends with `Async`.

Error handling:
- Prefer domain exceptions and set context fields:
  - `TranslationException` with `ErrorCode` and `ServiceId`.
  - `SidecarException` subclasses for IPC issues.
- Preserve inner exceptions when wrapping; do not swallow exceptions.
- Cancellation: `CancellationToken cancellationToken = default` last; do not turn cancellation into a generic error.

Async/concurrency:
- Avoid `.Result`/`.Wait()`; prefer `await`.
- If you must use `TaskCompletionSource`, use `TaskCreationOptions.RunContinuationsAsynchronously`.
- Use `ConcurrentDictionary`/locks for shared state; avoid racey event invocation.

TranslationService conventions:
- New services: add under `dotnet/src/Easydict.TranslationService/Services/`.
- Prefer deriving from `BaseTranslationService` (or `BaseOpenAIService` for OpenAI-compatible streaming).
- Map failures to `TranslationException` with the right `TranslationErrorCode` (network/timeout/rate-limit/etc.).
- Respect `TranslationRequest.TimeoutMs` and propagate the provided `CancellationToken`.
- Streaming services implement `IStreamTranslationService`:
  - Use `[EnumeratorCancellation] CancellationToken cancellationToken`.
  - Yield incremental chunks (not the fully-accumulated string).

Adding a new translation service (typical checklist):
- Implement `ITranslationService` (or derive from `BaseTranslationService`).
- Pick a stable `ServiceId` (lowercase, hyphenated) and a user-facing `DisplayName`.
- If it's LLM streaming, derive from `BaseOpenAIService` and implement:
  - `Endpoint`, `ApiKey`, `Model` (and override `RequiresApiKey` if not needed).
  - `TranslateStreamAsync` if behavior differs from the base implementation.
- Register the service in `dotnet/src/Easydict.TranslationService/TranslationManager.cs`.
- Add/adjust settings fields (API keys, endpoints, models) in `dotnet/src/Easydict.WinUI/Services/SettingsService.cs`.
- Add an icon in `dotnet/src/Easydict.WinUI/Assets/ServiceIcons/` if the UI expects one.
- Add tests under `dotnet/tests/Easydict.TranslationService.Tests/Services/`.

HTTP conventions:
- Reuse the provided `HttpClient`; do not new up clients per request.
- Prefer `HttpCompletionOption.ResponseHeadersRead` for streaming responses.
- On non-success HTTP status codes, throw a `TranslationException` with an appropriate `ErrorCode`.
- Do not log secrets (API keys, auth headers, full request bodies).

SidecarClient conventions:
- IPC is JSON Lines over stdio; keep messages single-line JSON.
- Timeouts should throw `SidecarTimeoutException` and include request id.
- When the process exits, fail outstanding requests promptly.

WinUI conventions:
- Keep code-behind thin; put logic into `Services/`.
- Avoid blocking the UI thread; marshal UI updates to UI thread when needed.

XAML conventions (keep it boring and consistent):
- Prefer `x:Name` for elements you access from code-behind.
- Keep resources/styles close to where they are used; avoid giant global dictionaries.
- Avoid long event handler chains; push logic into services/viewmodels where possible.

Test conventions:
- xUnit + FluentAssertions; AAA; small, focused tests.
- Avoid real HTTP/I/O; prefer mocks/fakes (see `MockHttpMessageHandler`).

Diagnostics / hygiene:
- Prefer `System.Diagnostics.Debug.WriteLine` for ad-hoc debugging; remove noisy logs before merging.
- Never commit secrets (API keys, tokens). Test inputs should use dummy keys.
- If you add new settings, ensure defaults are sensible and tests do not depend on user-local files.
