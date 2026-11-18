#!/usr/bin/env bash
# 合併前關卡：後端三個測試專案依序跑（不平行、不跑 sln），再在 PostgreSQL 上跑雙跑集（Category=Postgres），最後跑前端 test／typecheck／build。
# 用法：bash scripts/unattended/gate.sh [worktree 根目錄，預設目前 repo]
# 每一步都印 exit code；失敗時另列失敗的測試名稱（各最多數行）；任一步非 0，整支以 1 結束。
# PostgreSQL 由 Testcontainers 起容器（要 Docker）；設了 FORMWRIGHT_TEST_PG（可 CREATE DATABASE 的連線字串）就改用它、不碰 Docker。
set -u
ROOT="${1:-$(git rev-parse --show-toplevel)}"
fail=0
PG_IMAGE=postgres:16-alpine

# Docker Desktop 冷啟動常超過 60 秒：一開始沒在跑就先叫起來（不等），和後面的 SQLite 測試重疊
if [ -z "${FORMWRIGHT_TEST_PG:-}" ] && ! docker info >/dev/null 2>&1; then
  if [ "$(uname)" = Darwin ]; then open -a Docker >/dev/null 2>&1 && echo "docker: 沒在跑，已啟動 Docker Desktop（不等）"; fi
fi

cd "$ROOT/backend" || exit 2
for p in DomainTests ApplicationTests WebAPITests; do
  start=$(date +%s)
  FORMWRIGHT_TEST_DB=sqlite dotnet test "tests/DynamicModules.$p" 2>&1 | grep -E "^(Passed|Failed)!|^ +Failed [A-Za-z]" | tail -6
  code=${PIPESTATUS[0]}
  echo "$p exit=$code ($(( $(date +%s) - start ))s)"
  [ "$code" -ne 0 ] && fail=1
done

# PostgreSQL 雙跑集（PostgreSQL provider 設計 §6.5）：預算兩步合計 10 分鐘；超過就縮 WebAPITests 的清單，不放寬預算
pg_ready=1
if [ -z "${FORMWRIGHT_TEST_PG:-}" ]; then
  waited=0
  until docker info >/dev/null 2>&1; do
    if [ "$waited" -ge 60 ]; then pg_ready=0; break; fi
    sleep 5; waited=$((waited + 5))
  done
  if [ "$pg_ready" -eq 1 ]; then
    start=$(date +%s)
    docker pull -q "$PG_IMAGE" >/dev/null 2>&1; code=$?
    echo "docker pull $PG_IMAGE exit=$code ($(( $(date +%s) - start ))s，不算進預算)"
    [ "$code" -ne 0 ] && pg_ready=0
  else
    echo "PostgreSQL 測試環境無法啟動：Docker 沒有在跑（等了 60 秒），或設定 FORMWRIGHT_TEST_PG"
  fi
fi
if [ "$pg_ready" -eq 1 ]; then
  pg_start=$(date +%s)
  for p in ApplicationTests WebAPITests; do
    start=$(date +%s)
    FORMWRIGHT_TEST_DB=postgres dotnet test "tests/DynamicModules.$p" \
      --filter "Category=Postgres&Category!=SqliteOnly&Category!=Benchmark" 2>&1 \
      | grep -E "^(Passed|Failed)!|^ +Failed [A-Za-z]|(已通過|失敗)!|^ +失敗 [A-Za-z]" | tail -6
    code=${PIPESTATUS[0]}
    echo "$p (PostgreSQL) exit=$code ($(( $(date +%s) - start ))s)"
    [ "$code" -ne 0 ] && fail=1
  done
  pg_total=$(( $(date +%s) - pg_start ))
  echo "PostgreSQL 合計 ${pg_total}s（預算 600s）"
  [ "$pg_total" -gt 600 ] && echo "警告：PostgreSQL 雙跑集超過預算：縮 WebAPITests 的清單（保留並行與 O1 相關類別）"
else
  echo "PostgreSQL exit=1"; fail=1
fi

cd "$ROOT/frontend" || exit 2
bun run test 2>&1 | grep -E "Tests +[0-9]|^ *(FAIL|×) " | tail -6; code=${PIPESTATUS[0]}; echo "test exit=$code"; [ "$code" -ne 0 ] && fail=1
bun run typecheck >/dev/null 2>&1; code=$?; echo "typecheck exit=$code"; [ "$code" -ne 0 ] && fail=1
bun run build >/dev/null 2>&1; code=$?; echo "build exit=$code"; [ "$code" -ne 0 ] && fail=1

exit $fail
