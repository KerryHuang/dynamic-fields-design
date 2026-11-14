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
# watch 可能因網路中斷或睡眠提早返回：確認整個 run 完成才往下讀結果
until [ "$(gh run view "$id" --json status -q .status 2>/dev/null)" = completed ]; do sleep 30; done
gh run view "$id" --json jobs -q '.jobs[] | "\(.name)\t\(.conclusion)\t\(if (.startedAt|startswith("0001")) or (.completedAt|startswith("0001")) then "-" else (((.completedAt|fromdate) - (.startedAt|fromdate)) / 60 | floor | tostring) + "m" end)"'
gh run view "$id" --json jobs -q '.jobs[] | select(.conclusion == "failure") | .name' | sed 's/^/FAILED /'
c=$(gh run view "$id" --json jobs -q '.jobs[] | select(.name == "gate") | .conclusion')
echo "${c:-unknown}"
[ "$c" = success ]
