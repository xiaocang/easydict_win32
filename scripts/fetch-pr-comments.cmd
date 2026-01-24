@echo off
setlocal enabledelayedexpansion

REM Fetch PR comments by PR number using gh CLI
REM Usage: fetch-pr-comments.cmd <PR_NUMBER>

if "%~1"=="" (
    echo Usage: %~nx0 ^<PR_NUMBER^>
    echo Example: %~nx0 123
    exit /b 1
)

set "PR_NUMBER=%~1"

echo === PR #%PR_NUMBER% Review Comments ===
echo.

REM Fetch review comments (comments on code diffs)
gh api "repos/{owner}/{repo}/pulls/%PR_NUMBER%/comments" --jq ".[] | \"[\(.user.login)] \(.path):\(.line // .original_line)\n\(.body)\n---\"" 2>nul
if errorlevel 1 echo (No review comments or error fetching)

echo.
echo === PR #%PR_NUMBER% Issue Comments ===
echo.

REM Fetch issue comments (general conversation comments)
gh api "repos/{owner}/{repo}/issues/%PR_NUMBER%/comments" --jq ".[] | \"[\(.user.login)] \(.created_at)\n\(.body)\n---\"" 2>nul
if errorlevel 1 echo (No issue comments or error fetching)

endlocal
