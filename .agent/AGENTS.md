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

## Local Debug Tools

### PDF -> pics
- Purpose: render a PDF into per-page images for local inspection/debugging, using the repo's MuPDF-based helper.
- Tool: `dotnet/tools/PdfToImages/`
- Typical usage:
```bash
dotnet run --project dotnet/tools/PdfToImages -- --input <file.pdf>
dotnet run --project dotnet/tools/PdfToImages -- --input <file.pdf> --output-dir <dir> --dpi 144 --format png
dotnet run --project dotnet/tools/PdfToImages -- --input <file.pdf> --page 2
dotnet run --project dotnet/tools/PdfToImages -- --input <file.pdf> --page-range 2-4,7
```
- Notes:
  - Default output directory is `<pdf-name>_pages` beside the source PDF.
  - Supported formats are `png` and `jpg`.
  - `--page` exports a single page; `--page-range` supports comma-separated pages/ranges like `1-3,5`.
  - This is a developer utility, not a user-facing packaged feature.

### Local Long-Doc Translation CLI
- Purpose: locally debug the long-document translation pipeline from command line without going through the GUI.
- Wrapper script: `scripts/translate-long-doc.ps1`
- Underlying entry: `dotnet/src/Easydict.WinUI/Program.cs` + `Services/LongDocumentCliCommand.cs`
- Typical usage:
```powershell
powershell -File scripts/translate-long-doc.ps1 `
  -InputFile "C:\path\paper.pdf" `
  -TargetLanguage zh `
  -EnvFile ".env"
```
```powershell
powershell -File scripts/translate-long-doc.ps1 `
  -InputFile "C:\path\paper.pdf" `
  -TargetLanguage zh `
  -Page 2
```
```powershell
powershell -File scripts/translate-long-doc.ps1 `
  -InputFile "C:\path\paper.pdf" `
  -TargetLanguage zh `
  -PageRange "2-4,7"
```
```bash
dotnet run --project dotnet/src/Easydict.WinUI -p:WindowsPackageType=None -p:EnableLocalDebugLongDocCli=true -- --translate-long-doc --input <file> --target-language <lang> [options]
```
- Useful options:
  - `--service <id>`: choose translation service
  - `--output <path>`: override output path
  - `--page 2`: translate a single PDF page
  - `--page-range 1-3,5`: limit PDF pages
  - `--list-services`: list available long-doc-capable services
- Important packaging rule:
  - This CLI is local-debug-only.
  - It is only compiled when `EnableLocalDebugLongDocCli=true` (default only for local `Debug + WindowsPackageType=None`).
  - It must not be included in packaged `MSIX`, published `.zip`, or installer `.exe` artifacts.
  - The PowerShell wrapper under `scripts/` is repo-only and is not part of publish outputs.

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
