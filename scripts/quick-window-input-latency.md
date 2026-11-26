# Quick-window input latency (#258)

## Status and scope

This is a candidate fix for [issue #258](https://github.com/xiaocang/easydict_win32/issues/258).
The reported roughly 0.5 second delay has not been reproduced or measured on a
Windows desktop during this work. The deterministic regression below proves that
a confirmed empty UIA selection no longer waits for clipboard capture. Windows
validation must establish whether that path explains the reporter's case.

## Timing found in the code

The Mini hotkey records the foreground source, shows the window without activation,
captures the selection, then activates the window and requests input focus.

| Stage | Previous behavior | Change / remaining limit |
| --- | --- | --- |
| Window visibility | `ShowWithoutActivation` preserves the source selection | Preserved |
| Before UIA | Unconditional 150 ms delay in `OnShowMiniWindowHotkey` | Removed for the Mini hotkey |
| UIA result | Empty selection and unavailable provider both fell through to clipboard | Distinguish confirmed empty from unavailable |
| Clipboard fallback | 100 ms pre-copy wait, then bounded clipboard polling; ordinary timeout 450 ms, Electron timeout 1200 ms | Skipped for confirmed empty UIA results; retained for unavailable providers and the existing Electron-first path |
| Input focus | Up to 10 attempts with 50 ms between retries, plus dispatcher scheduling | Show and activation attempt focus inline when ready; one request per show; stale requests are cancelled |
| Auto-close | 500 ms grace period suppresses hiding after show | Preserved; this grace period does not block typing |

The 9 x 50 ms retry budget is 450 ms of scheduled waits. It is not an unconditional
delay or a total latency bound. A successful first focus attempt returns immediately,
and dispatcher scheduling can add time. Likewise, `TryEnqueue` and `Activate` alone
do not prove the input is ready. Check the foreground HWND and focused element.

An empty result is accepted only when all attempted UIA providers confirm empty.
UIA errors, null results, conflicting provider results and timeouts still permit
the existing fallback. Unsupported patterns are skipped; if no supported pattern
can confirm the result, fallback remains available. Terminal protection, automatic-copy allow lists, foreground
source validation, cancellation and clipboard cleanup remain in place. No change
is made to the Fixed-window hotkey's existing delay.

## Deterministic red / green regression

Run the actual production policy and lifecycle code without WinUI:

```sh
dotnet test dotnet/tests/Easydict.SelectionCapture.Tests/Easydict.SelectionCapture.Tests.csproj
```

The project links the production `SelectionCaptureSession`, `UiaSelectionResult`
and `HotkeyWindowShowCoordinator`, and the same test files included in the Windows
unit-test project. It does not emulate a UIA provider or Win32 focus.

The regression is
`ConfirmedEmptyUia_CompletesPendingWindowWithoutWaitingForClipboard`. It supplies
a confirmed empty selection and a clipboard operation that has not completed.
The window must exit the pending phase without invoking that clipboard operation.
There is no wall-clock threshold or arbitrary sleep in this test.

| Check | Result |
| --- | --- |
| RED, commit `827a03f` (extracted production method keeps the previous fallback policy) | 1 failed, 34 passed |
| GREEN, same 35 tests with the confirmed-empty early return | 35 passed |
| Final focused suite, including multi-range and provider disagreement/error cases | 58 passed |
| Existing foreground-toggle source-contract test | 1 passed (static source checks) |
| Windows UI automation test project | Cross-compiled on Linux, 0 warnings / 0 errors; desktop tests not executed |

To reproduce RED without changing your current checkout:

```sh
git worktree add --detach ../easydict-issue258-red 827a03f
dotnet test ../easydict-issue258-red/dotnet/tests/Easydict.SelectionCapture.Tests/Easydict.SelectionCapture.Tests.csproj
```

## Windows verification

Build the unpackaged app with `EasydictUiTestBuild=true`, using the existing UI
automation setup in `dotnet/tests/Easydict.UIAutomation.Tests`. Set
`EASYDICT_EXE_PATH` to that build's `Easydict.WinUI.exe` so the tests exercise this
branch instead of discovering an installed release. Then run:

```powershell
dotnet test dotnet/tests/Easydict.UIAutomation.Tests/Easydict.UIAutomation.Tests.csproj `
  --filter "FullyQualifiedName~MiniWindowFocusTests|FullyQualifiedName~WindowShowLatencyTests"
```

The new `MiniWindow_EmptySelection_AcceptsTypingWithoutFocusAssistanceOnOpenAndReopen`
case starts from an external text editor with an empty UIA selection and sends the
registered Ctrl+Alt+M shortcut. It observes foreground ownership and keyboard focus,
then types without calling `SetForeground`, `Focus` or `Click` on the Mini window.
A second text fragment is entered after 600 ms to detect a late `SelectAll` replacing
the first fragment. Both initial opening and reopening are exercised.

Its observed timing includes UIA polling and hotkey delivery overhead; it is a
functional test and timing probe, not proof of a sub-second performance target.
The existing `WindowShowLatencyTests` cover delayed capture without stealing focus,
the pending window surviving the auto-close grace period, cancellation by input,
and repeated-hotkey dismissal.

Before marking the PR ready, verify these cases on Windows:

- Confirmed empty selection in an external editor, initial open and warm reopen.
- Nonempty selection remains intact until capture completes and is then translated.
- Unsupported UIA provider and Electron fallback still retrieve selected text.
- Manual typing, IME composition, a pointer action or the saved-items menu prevents
  delayed focus requests from taking over the user's current interaction.
- Repeated hotkey, hide/reopen and switching to another application discard stale
  requests and preserve auto-close and pin behavior.
- Automatic mouse selection and terminal exclusions retain their existing behavior.

Use `%LOCALAPPDATA%\Easydict\debug.log` to separate capture, activation and confirmed
input-focus time:

- `[WindowShow] Mini: capture started/complete` measure from the hotkey handler.
- `[TextSelectionService] stage=UIA/clipboard` report each manual-capture stage's
  elapsed time and outcome. Automatic mouse selection does not add these disk logs.
- `[WindowShow] Mini: visible without activation`, `view activation requested`,
  `foreground result`, `activation event`, and `input focus confirmed` use the
  window's show stopwatch. A no-activation show and its later activation share
  that stopwatch; direct activated shows start a new one.

The two stopwatch origins are distinct, so compare stages within their own clock.
The added diagnostics record timing and state only, with no selected text, typed
text or key values. Compare the reporter's application and shortcut with these
paths before claiming the reported delay is resolved. Full WinUI application
build and Windows desktop validation remain pending.

## API references

- [UIA Text and TextRange patterns](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-implementingtextandtextrange): a caret with no selection is represented by a degenerate text range.
- [UIElement.Focus](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.uielement.focus): its result distinguishes keyboard/logical focus changes.
- [DispatcherQueue.TryEnqueue](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.dispatching.dispatcherqueue.tryenqueue): success means the callback was queued.
- [SetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow): foreground activation remains subject to Windows restrictions.
- [TextBox.TextChanging](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.textbox.textchanging): synchronous text changes invalidate focus requests before a subsequent show can create a new one.
