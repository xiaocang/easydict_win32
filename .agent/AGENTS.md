# AGENTS.md (easydict_win32)

This file is the agent-focused operating guide for this repository.
It is aligned with `.claude/CLAUDE.md` (project source-of-truth for architecture and conventions).

## Scope & Priority
- Repository scope: entire repo rooted at `easydict_win32/`.
- If this file conflicts with direct user/developer/system instructions, follow prompt instructions first.
- For project details and deeper context, consult `.claude/CLAUDE.md`.

## Working Agreement
- Keep diffs small and reviewable; match nearby code style.
- Avoid sweeping refactors or mass formatting unless explicitly requested.
- For bug fixes/behavior changes, prefer adding or updating tests.
- Preserve existing architecture boundaries (UI vs service/business logic).

## Repo Landmarks
- Solution: `dotnet/Easydict.Win32.sln`
- App: `dotnet/src/Easydict.WinUI/`
- Translation core: `dotnet/src/Easydict.TranslationService/`
- Sidecar client: `dotnet/src/Easydict.SidecarClient/`
- Tests: `dotnet/tests/`

## Build / Test (from `dotnet/`)
```bash
dotnet restore Easydict.Win32.sln
dotnet build Easydict.Win32.sln -c Debug --no-restore
dotnet test Easydict.Win32.sln --no-build --verbosity normal
```

Useful focused commands:
```bash
dotnet test tests/Easydict.TranslationService.Tests --filter "FullyQualifiedName~<TestName>"
dotnet test tests/Easydict.WinUI.Tests --logger "console;verbosity=minimal"
```

## Code Style (match existing)
- Modern C# conventions already used in repo: nullable enabled, file-scoped namespaces, `required`/`init`, async-first.
- 4-space indentation, braces on new lines, early returns preferred.
- Naming: PascalCase for types/methods, `_camelCase` fields, async methods end with `Async`.
- Do not swallow exceptions; preserve cancellation semantics.
- Keep WinUI code-behind lean; move reusable logic into `Services/` where practical.

## Critical Alignment Rules from CLAUDE.md

### 1) Version bump checklist
When updating app version, update both files:
- `dotnet/src/Easydict.WinUI/Easydict.WinUI.csproj` (`Version`, `AssemblyVersion`, `FileVersion`)
- `dotnet/src/Easydict.WinUI/Package.appxmanifest` (`Identity Version`)

### 2) Documentation sync
`README.md` and `README_ZH.md` must stay synchronized in structure/content whenever one changes.

### 3) PR review comment retrieval fallback
If `gh` CLI is unavailable, use GitHub REST endpoints:
- `/pulls/{pr}/comments` (inline review comments)
- `/pulls/{pr}/reviews` (top-level reviews)
- `/issues/{pr}/comments` (conversation comments)
- `/pulls/{pr}` (PR metadata)

### 4) Error handling & cancellation
- Use domain exceptions (`TranslationException`, `SidecarException` hierarchy) where applicable.
- Keep `CancellationToken cancellationToken = default` as last optional parameter.
- Never convert cancellation into generic failures.

## Safety / Hygiene
- Never commit secrets.
- Prefer deterministic, hermetic tests (no real network dependency).
- Remove noisy temporary debug logs before finalizing.

## Notes on environment mismatch
- Some CI/Windows-specific commands may be unavailable in Linux/sandbox environments.
- If a required runtime/tool is missing, report clearly and still complete static verification.
