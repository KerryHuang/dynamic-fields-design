#!/usr/bin/env bash
# 合併前本機走查用：以全新 Demo 資料啟動後端（5180，開 Demo 一鍵登入）與前端 dev（9180）。
# 用法：bash scripts/unattended/local-demo.sh [worktree 根目錄，預設目前 repo]
# 前景執行到被停止為止（請用背景執行）；就緒時印 READY。資料庫另放 local-e2e.db，不碰 poc.db。
set -u
ROOT="${1:-$(git rev-parse --show-toplevel)}"
cd "$ROOT/poc" || exit 2

for port in 5180 9180; do
  if lsof -nP -iTCP:"$port" -sTCP:LISTEN >/dev/null 2>&1; then
    echo "port $port 已被占用（可能有另一份 POC 在跑），請先停掉再試" >&2
    exit 2
  fi
done
[ -d frontend/node_modules ] || (cd frontend && bun install) || exit 1

DB="$ROOT/poc/backend/data/local-e2e.db"
rm -f "$DB" "$DB-shm" "$DB-wal"
export ConnectionStrings__Dynamic="Data Source=$DB" ASPNETCORE_ENVIRONMENT=Development
API=backend/src/DynamicModules.WebAPI

echo "產生 Demo 資料 ..."
dotnet run --project "$API" -- --generate-demo-data >/dev/null 2>&1 || { echo "Demo 資料產生失敗" >&2; exit 1; }

Demo__LoginEnabled=true dotnet run --no-build --project "$API" >/dev/null 2>&1 &
BACKEND=$!
(cd frontend && exec bunx quasar dev >/dev/null 2>&1) &
FRONTEND=$!

cleanup() {
  trap - INT TERM HUP EXIT
  for pid in $BACKEND $FRONTEND; do
    pkill -TERM -P "$pid" 2>/dev/null
    kill -TERM "$pid" 2>/dev/null
  done
  wait 2>/dev/null
}
trap cleanup INT TERM HUP EXIT

for url in http://127.0.0.1:5180/api/health http://127.0.0.1:9180/; do
  i=0
  until curl -s -o /dev/null "$url"; do
    i=$((i + 1)); [ "$i" -ge 180 ] && { echo "逾時：$url 沒有回應" >&2; exit 1; }
    sleep 1
  done
done
echo "READY http://127.0.0.1:9180"
wait
