# UI demo recordings — local and CI

Records the real WinUI app while a UI automation scenario drives it, converts the capture into a
small GIF (default: 75% scale, 2x speed), and optionally publishes it to the pull request so it plays
inline.

The owner asked for one script that runs both locally and in CI, and that only ever records the
current PR's scenarios.

## What a scenario is

A scenario is "a UI test filter that drives the app". The recording script does not script the UI
itself: it starts the capture, runs `dotnet test --filter <expression>`, stops the capture, and turns
the result into a GIF. That keeps the recorded operations identical to what the automated test does.

`dotnet/scripts/demo/demo-scenarios.json` lists the scenarios for the current PR:

```json
[
  {
    "name": "ollama-refresh-inplace",
    "description": "Refresh fills the model combo in place, no picker dialog",
    "testFilter": "FullyQualifiedName~AiProfileModelRefreshTests.RefreshOllama",
    "windowTitle": "EasyDict",
    "maxSeconds": 25
  }
]
```

Keep this list small and specific to the PR: it is a demo, not a recording of the whole suite.

## Local use

```powershell
# Build the UI-test build first (same one the automation suite uses)
dotnet publish dotnet/src/Easydict.WinUI/Easydict.WinUI.csproj -c Release -r win-x64 `
  --self-contained true -p:Platform=x64 -p:EasydictUiTestBuild=true -o dotnet/publish/x64

# Record one scenario
dotnet/scripts/demo/Invoke-UiDemoRecording.ps1 `
  -ScenarioName ollama-refresh-inplace `
  -TestFilter "FullyQualifiedName~AiProfileModelRefreshTests.RefreshOllama" `
  -AppExePath dotnet/publish/x64/Easydict.WinUI.exe `
  -OutputDir artifacts/ui-demos
```

Record on a **blank desktop**, with the app maximised or at a fixed size, and make sure no other agent
or UI test is using the desktop at that moment.

## CI use

`.github/workflows/ui-demo.yml` runs on a pull request labelled `ui-demo` (opt-in, so ordinary PRs pay
nothing). It builds the test build, records every scenario in the manifest, publishes the GIFs and
upserts one comment on the PR.

Publishing needs `contents: write` to push to the data branch. Pull requests from forks cannot push,
so the workflow skips publishing there and says so instead of failing.

## How publishing works

- The GIF is pushed to the `scratch/pr-demos` data branch as
  `pr-<number>/<sha>/<scenario>.gif`. Nothing is committed to `master`.
- The PR comment embeds `https://raw.githubusercontent.com/<owner>/<repo>/scratch/pr-demos/<path>` so
  the GIF plays inline. An artifact link cannot do that.
- The SHA in the path keeps the raw CDN from serving a stale image after a new push.
- One comment per PR, found by a hidden marker (`<!-- ui-demo:<scenario> -->`) and updated in place,
  so iterations do not spam the thread.
- The original `.mp4` stays a CI artifact with the usual short retention; only the GIF is kept.
## Handshake with the scenario (required)

A recording that starts before the app exists, or that begins after the operations already ran, is
worthless. The recorder and the demo test therefore agree on three things:

- the recorder creates a directory and exports `EASYDICT_DEMO_HANDSHAKE_DIR`;
- the test writes `ready.json` once the app and the target page are ready:
  `{ "pid": 12345, "hwnd": 67890, "exePath": "…\\Easydict.WinUI.exe", "windowTitle": "EasyDict" }`;
- the recorder proves the HWND belongs to that PID, starts the capture, waits until ffmpeg reports
  its **first frame** (`-progress pipe:2`, so a fixed sleep can never be mistaken for evidence), and
  only then writes `continue`; the test waits for that file before performing the operations.

If the app never appears, the PID is dead, the HWND belongs to another process, or ffmpeg exits
before producing a frame, the run fails instead of recording something misleading.

## Pull request body vs. comment

- The **first delivery** of a PR puts **every scenario** of that run into one `<!-- ui-demo-body -->`
  region in the PR body — one block per scenario, keyed by `<!-- ui-demo:<scenario> -->`.
- A scenario already present in the body is left alone on later runs: subsequent commits only update
  the scenario's comment, so the body stays stable.
- Only comments authored by `github-actions[bot]` are ever modified.

## Trusted publisher and the first landing

The publish job holds the write token, so it never executes code from the pull request. It uses the
publisher from the **default branch**. On the very first PR that introduces this tooling the default
branch does not have it yet; in that case the repository variable `DEMO_SCRIPT_REF` must hold a
**40-character commit SHA** of an independently reviewed revision, and only `dotnet/scripts/demo/` is
materialised from it. A non-SHA value fails the job; an arbitrary PR head is never used.