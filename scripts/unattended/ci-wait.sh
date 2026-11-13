#!/usr/bin/env bash
# 等 GitHub Actions 的 gate 跑完：bash scripts/unattended/ci-wait.sh <sha>
# 最後一行是 gate 的結論（success／failure／cancelled…）；失敗時先列出失敗的 job。
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
gh run watch "$id" --interval 30 >/dev/null 2>&1
gh run view "$id" --json jobs -q '.jobs[] | "\(.name)\t\(.conclusion)\t\(((.completedAt|fromdate) - (.startedAt|fromdate)) / 60 | floor)m"'
gh run view "$id" --json jobs -q '.jobs[] | select(.conclusion == "failure") | .name' | sed 's/^/FAILED /'
c=$(gh run view "$id" --json jobs -q '.jobs[] | select(.name == "gate") | .conclusion')
echo "${c:-unknown}"
[ "$c" = success ]
