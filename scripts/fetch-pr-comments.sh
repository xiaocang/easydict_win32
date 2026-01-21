#!/bin/bash

# Fetch PR comments by PR number using gh CLI
# Usage: ./fetch-pr-comments.sh <PR_NUMBER>

set -e

if [ -z "$1" ]; then
    echo "Usage: $0 <PR_NUMBER>"
    echo "Example: $0 123"
    exit 1
fi

PR_NUMBER="$1"

echo "=== PR #${PR_NUMBER} Review Comments ==="
echo ""

# Fetch review comments (comments on code diffs)
gh api "repos/{owner}/{repo}/pulls/${PR_NUMBER}/comments" --jq '.[] | "[\(.user.login)] \(.path):\(.line // .original_line)\n\(.body)\n---"'

echo ""
echo "=== PR #${PR_NUMBER} Issue Comments ==="
echo ""

# Fetch issue comments (general conversation comments)
gh api "repos/{owner}/{repo}/issues/${PR_NUMBER}/comments" --jq '.[] | "[\(.user.login)] \(.created_at)\n\(.body)\n---"'
