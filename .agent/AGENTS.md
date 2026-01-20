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

Platform notes:
- WinUI projects build/test on Windows runners.
- `Easydict.TranslationService` + its tests are cross-platform.

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

Makefile shortcuts (same folder):

```bash
make build
make build-debug
make build-release
make test
make test-translation
make test-winui
make publish
```

## Lint / Formatting

No repo-wide formatter configuration is checked in (no `.editorconfig`).
Treat compiler + analyzers during `dotnet build` as the primary lint signal.

Useful local checks:

```bash
# Optional: fail on warnings
dotnet build Easydict.Win32.sln -c Debug -warnaserror

# Optional: run dotnet-format (only if installed; do not mass-reformat)
dotnet format Easydict.Win32.sln
```

## Tests

CI runs on Windows and executes solution-wide tests:

```bash
dotnet test Easydict.Win32.sln --no-build --verbosity normal
```

Run one test project:

```bash
dotnet test tests/Easydict.TranslationService.Tests --logger "console;verbosity=minimal"
dotnet test tests/Easydict.WinUI.Tests --logger "console;verbosity=minimal"
```

Run a single test (xUnit) using `--filter`:

```bash
# Discover exact names
dotnet test tests/Easydict.TranslationService.Tests --list-tests

# Single class
dotnet test tests/Easydict.TranslationService.Tests --filter "FullyQualifiedName~TranslationManagerTests"

# Single method
dotnet test tests/Easydict.TranslationService.Tests --filter "FullyQualifiedName~GoogleTranslateServiceTests.TranslateAsync_ReturnsTranslatedText"
```

Notes:
- Prefer filtering by `FullyQualifiedName~...` (more stable than display names).
- Keep tests hermetic: no real network calls; use mocks (e.g., `MockHttpMessageHandler`).

## Code Style (Match Existing Code)

This codebase already uses modern C# patterns (nullable enabled, file-scoped namespaces,
`required` properties, raw string literals in tests). Keep changes consistent.

### Files / Project Structure

- Prefer file-scoped namespaces: `namespace Easydict.X;`.
- Keep namespaces aligned with folders (`Easydict.TranslationService.Services`, etc.).
- Prefer one primary type per file; filename matches the type.
- Put WinUI UI logic in `Views/` and non-UI logic in `Services/`.

### Imports

- `ImplicitUsings` is enabled; add explicit `using` only when needed.
- WinUI global usings live in `dotnet/src/Easydict.WinUI/Imports.cs`.
- Ordering for explicit `using` blocks:
  - `System.*`
  - `Easydict.*`
  - third-party (`Microsoft.*`, `Xunit`, `FluentAssertions`, etc.)
- Separate groups with a single blank line.

### Formatting

- 4-space indent; braces on new lines.
- Prefer early returns over deep nesting.
- Prefer object/collection initializers for DTO-like objects.
- Prefer pattern matching (`is null`, `is not null`) over `== null` when it reads better.
- Use raw string literals (`"""`) for multi-line JSON in tests.

### Types / Nullability

- Nullable reference types are enabled; annotate correctly (`string?`, `Foo?`).
- Prefer `required` + `init` for request/option models (see `TranslationRequest`).
- Avoid null-forgiving (`!`) unless you can justify a strong invariant.
- Prefer `IReadOnlyList<T>`/`IReadOnlyDictionary<K,V>` for exposed collections.

### Naming

- Public API: `PascalCase`.
- Locals/parameters: `camelCase`.
- Private fields: `_camelCase`.
- Async methods: `*Async` and return `Task`/`Task<T>`.
- Domain conventions: `*Service`, `*Options`, `*Exception`, `*Tests`.
- Test method naming convention: `Method_Scenario_ExpectedResult`.

### Error Handling

- Use specific exception types and include context.
  - Translation domain: prefer `TranslationException` + `TranslationErrorCode`.
  - Sidecar domain: prefer `SidecarException` subclasses (timeout, not running, etc.).
- Preserve the original exception as `innerException` when wrapping.
- Do not swallow exceptions silently; if intentionally ignored, add a short comment.
- Cancellation:
  - Put `CancellationToken cancellationToken = default` last in the parameter list.
  - Do not convert cancellation into generic failures unless explicitly required.

### Async / Concurrency

- Avoid blocking calls (`.Result`, `.Wait()`) and sync-over-async.
- Prefer `TaskCreationOptions.RunContinuationsAsynchronously` with `TaskCompletionSource`.
- Use `ConcurrentDictionary`/locks for shared state (see `SidecarClient`).
- Dispose/cleanup resources deterministically (`IDisposable`/`IAsyncDisposable`).

### WinUI Notes

- Keep code-behind thin; prefer services for state and side effects.
- Do not block UI thread; keep long work async.
- Marshal UI updates back to UI thread (DispatcherQueue, etc.) when needed.

### Test Style

- Use xUnit + FluentAssertions.
- Prefer AAA (Arrange/Act/Assert) with small, focused tests.
- Prefer `Assert.ThrowsAsync<T>` / FluentAssertions async helpers for exception tests.
- Avoid real I/O and network; use fakes/mocks.
