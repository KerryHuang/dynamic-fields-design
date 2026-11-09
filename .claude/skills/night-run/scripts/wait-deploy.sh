#!/usr/bin/env bash
# 等託管平台對某個 commit 的部署結束（GitHub deployments API），最多約 30 分鐘。
# 用法：bash .claude/skills/night-run/scripts/wait-deploy.sh <commit sha>
# 以 run_in_background 執行，等完工通知，不要輪詢。結束時最後一行是 success／failure／error／timeout。
set -u
SHA="$1"
REPO=$(gh repo view --json nameWithOwner --jq .nameWithOwner)
for _ in $(seq 1 60); do
  ID=$(gh api "repos/$REPO/deployments?sha=$SHA" --jq '.[0].id' 2>/dev/null)
  if [ -n "$ID" ]; then
    S=$(gh api "repos/$REPO/deployments/$ID/statuses" --jq '.[0].state' 2>/dev/null)
    echo "$(date +%H:%M) $S"
    case "$S" in success) exit 0 ;; failure|error) exit 1 ;; esac
  else
    echo "$(date +%H:%M) no deployment yet"
  fi
  sleep 30
done
echo timeout
exit 1
