#!/usr/bin/env bash
# 合併前關卡：後端三個測試專案依序跑（不平行、不跑 sln），再跑前端 test／typecheck／build。
# 用法：bash .claude/skills/unattended/scripts/gate.sh [worktree 根目錄，預設目前 repo]
# 每一步都印 exit code；失敗時另列失敗的測試名稱（各最多數行）；任一步非 0，整支以 1 結束。
set -u
ROOT="${1:-$(git rev-parse --show-toplevel)}"
fail=0

cd "$ROOT/poc/backend" || exit 2
for p in DomainTests ApplicationTests WebAPITests; do
  start=$(date +%s)
  dotnet test "tests/DynamicModules.$p" 2>&1 | grep -E "^(Passed|Failed)!|^ +Failed [A-Za-z]" | tail -6
  code=${PIPESTATUS[0]}
  echo "$p exit=$code ($(( $(date +%s) - start ))s)"
  [ "$code" -ne 0 ] && fail=1
done

cd "$ROOT/poc/frontend" || exit 2
bun run test 2>&1 | grep -E "Tests +[0-9]|^ *(FAIL|×) " | tail -6; code=${PIPESTATUS[0]}; echo "test exit=$code"; [ "$code" -ne 0 ] && fail=1
bun run typecheck >/dev/null 2>&1; code=$?; echo "typecheck exit=$code"; [ "$code" -ne 0 ] && fail=1
bun run build >/dev/null 2>&1; code=$?; echo "build exit=$code"; [ "$code" -ne 0 ] && fail=1

exit $fail
