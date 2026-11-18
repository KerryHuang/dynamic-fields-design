#!/usr/bin/env bash
# 等 GitHub Actions 的 gate 跑完：bash scripts/unattended/ci-wait.sh <sha>
# 最後一行是 gate 的結論（success／failure／cancelled…）；失敗時先列出失敗的 job。
# 任一 job 一失敗就回報 failure，不等整個 run 跑完。
# exit 0 只代表 gate 通過；其他情況 exit 1。
set -u
sha=$(git rev-parse "${1:?用法：ci-wait.sh <sha>}")
id=""
for _ in $(seq 1 60); do
  id=$(gh run list --workflow gate.yml --commit "$sha" --limit 1 --json databaseId -q '.[0].databaseId' 2>/dev/null)
  [ -n "$id" ] && break
  sleep 10
done
[ -n "$id" ] || { echo "找不到 $sha 的 gate run（分支推上去了嗎？）"; echo timeout; exit 1; }
# 每 30 秒看一次：整個 run 完成，或任一 job 已失敗（不等其他 job 跑完）就往下報
while :; do
  st=$(gh run view "$id" --json status,jobs -q '"\(.status) \([.jobs[] | select(.conclusion == "failure")] | length)"' 2>/dev/null)
  case "$st" in
    completed*) break ;;
    *" "[1-9]*) early=1; break ;;
  esac
  sleep 30
done
gh run view "$id" --json jobs -q '.jobs[] | "\(.name)\t\(.conclusion)\t\(if (.startedAt|startswith("0001")) or (.completedAt|startswith("0001")) then "-" else (((.completedAt|fromdate) - (.startedAt|fromdate)) / 60 | floor | tostring) + "m" end)"'
gh run view "$id" --json jobs -q '.jobs[] | select(.conclusion == "failure") | .name' | sed 's/^/FAILED /'
c=$(gh run view "$id" --json jobs -q '.jobs[] | select(.name == "gate") | .conclusion')
[ "${early:-}" = 1 ] && { echo "（run 仍在進行，首個失敗即回報；其餘 job 未等）"; echo failure; exit 1; }
echo "${c:-unknown}"
[ "$c" = success ]
