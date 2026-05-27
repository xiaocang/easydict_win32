---
name: review-local-ui-screenshots
description: Review Easydict UI automation screenshot artifacts already present in local artifacts/ui-screenshots, screenshots, or a user-provided artifact directory. Use when asked to inspect local UI test screenshots, validate whether screenshot contents match filenames, review visual-diffs, baseline-candidates, failure snapshots, or summarize visual regressions from local artifacts.
---

# Review Local UI Screenshots

## Goal

Review screenshots as visual evidence. Check whether the visible UI state matches the meaning implied by the file name and directory, then report actionable mismatches first.

## Locate Artifacts

Use the user-provided directory if present. Otherwise check, in order:

```powershell
artifacts/ui-screenshots
artifacts
screenshots
```

List images recursively and keep paths relative to the artifact root in the report. Prefer inspecting individual screenshots over only reading gallery/contact-sheet images.

```powershell
Get-ChildItem -Recurse -File artifacts/ui-screenshots -Include *.png,*.jpg,*.jpeg |
  Sort-Object FullName
```

## Review Priority

Inspect in this order:

1. `visual-diffs/` and `*_diff.png`: likely visual regression failures. Compare against nearby actual/baseline/candidate screenshots when available.
2. `baseline-candidates/`: new or changed baseline candidates that need human approval.
3. Diagnostic failure snapshots: names containing `not_found`, `failed`, `failure`, `missing`, `error`, or `navigation_failed`.
4. Regular workflow screenshots.

Do not treat diagnostic names as passing states. A screenshot named `overlay_not_found` means the test captured a failure context; verify whether the screenshot explains that failure.

## Filename Semantics

Infer expected state from path and filename:

- `before`, `initial`: starting state, no translated result unless the scenario says otherwise.
- `after`, `result`, `translate`, `translated`: result content should be visible and the app should still be usable.
- `light`, `dark`, `follow-system`: palette should match the named theme; look for mixed light/dark controls.
- `settings_*`: the named settings page, tab, expander, or scroll section should be visible.
- `ocr`, `overlay`, `capture`: capture overlay or OCR state should be visible when named; after-cancel/dismiss screenshots should show recovery.
- `mini`, `fixed`, `main`: the named window type should be the primary subject.
- `workflow_XX`, `step`, `streaming_XX`: screenshots should form a plausible sequence with monotonic or expected UI progression.
- `fullscreen`: expect desktop/app context; tolerate environmental noise but flag wrong app focus or missing target UI.

## Visual Checks

For each reviewed image, inspect pixels with the available image viewer. Flag:

- blank, black, tiny, corrupted, or wrong-window screenshots
- content that contradicts the filename or directory
- clipped, offscreen, hidden, or overlapped target UI
- wrong theme, mixed palette, unreadable contrast, or stale state
- expected controls/results missing from a named state
- unintended dialogs, taskbar popups, desktop clutter, or other apps covering the target
- secrets or personal data visible in a screenshot intended for CI review

When there are many screenshots, triage with `ui-screenshot-gallery.jpg` or generate one with:

```powershell
dotnet/scripts/ci/Publish-UiScreenshotSummary.ps1 `
  -ScreenshotRoot artifacts/ui-screenshots `
  -ArtifactName local-ui-screenshots `
  -Title "Local UI screenshot review" `
  -SummaryPath artifacts/ui-screenshots/local-review-summary.md
```

Then open individual priority images before making findings.

## Report Format

Lead with findings. For each finding include:

- severity: `High`, `Medium`, or `Low`
- screenshot path
- expected state inferred from the file name
- observed state
- why this is a mismatch
- suggested next check or fix

After findings, add:

- coverage: which artifact root and screenshot groups were reviewed
- pass summary: notable groups that matched their names
- skipped/not reviewed: any large groups intentionally sampled, with reason

If no issues are found, say that clearly and mention any residual risk such as sampled screenshots or missing baseline comparisons.
