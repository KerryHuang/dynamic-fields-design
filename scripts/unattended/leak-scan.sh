#!/usr/bin/env bash
# 外洩掃描：比對 <base>..HEAD 的 diff、commit 訊息、檔名，只印命中筆數，絕不印出字串。
# 用法：bash scripts/unattended/leak-scan.sh [base，預設 origin/main]
# 字串清單放在 repo 外的本機檔，路徑由 LEAK_DENYLIST 指定；沒設定就停下（不當作通過）。
set -u
BASE="${1:-origin/main}"
LIST="${LEAK_DENYLIST:-}"
if [ -z "$LIST" ] || [ ! -f "$LIST" ]; then
  echo "LEAK_DENYLIST 未設定或檔案不存在，掃描未執行" >&2
  exit 2
fi
P=$(grep -v '^\s*#' "$LIST" | grep -v '^\s*$' | sed 's/^regex://' | paste -sd'|' -)
P="$P|/Users/[a-z]+/|/home/[a-z]+/|192\.168\.|10\.[0-9]+\.[0-9]+\.[0-9]+"
d=$(git diff "$BASE"..HEAD | grep -Eic "$P")
l=$(git log "$BASE"..HEAD --format=%B | grep -Eic "$P")
n=$(git diff --name-only "$BASE"..HEAD | grep -Eic "$P")
echo "diff=$d log=$l names=$n"
[ "$d" -eq 0 ] && [ "$l" -eq 0 ] && [ "$n" -eq 0 ]
