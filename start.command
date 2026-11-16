#!/usr/bin/env bash
# 啟動 POC：後端 (5180) + 前端 (9180)。Ctrl-C 同時停止。NO_OPEN=1 時不開瀏覽器。
cd "$(dirname "$0")" || exit 1

[ -d frontend/node_modules ] || (cd frontend && bun install) || exit 1

ASPNETCORE_ENVIRONMENT=Development dotnet run --project backend/src/DynamicModules.WebAPI &
BACKEND=$!
(cd frontend && exec bunx quasar dev) &
FRONTEND=$!

cleanup() {
  trap - INT TERM HUP EXIT
  # 子程序可能再衍生孫程序（dotnet、bun、node），一併收掉
  for pid in $BACKEND $FRONTEND; do
    pkill -TERM -P "$pid" 2>/dev/null
    kill -TERM "$pid" 2>/dev/null
  done
  wait 2>/dev/null
  exit "${FAILED:-0}"
}
trap cleanup INT TERM HUP EXIT

# 等 URL 回應；逾時（秒）就印訊息並退出（EXIT trap 會清掉子程序）
wait_for() {
  local url=$1 name=$2 timeout=$3 i=0
  echo "等待 $name ($url) ..."
  until curl -s -o /dev/null "$url"; do
    i=$((i + 1))
    if [ "$i" -ge "$timeout" ]; then
      echo "逾時：$name 在 ${timeout} 秒內沒有回應，請看上方輸出的錯誤。" >&2
      FAILED=1
      exit 1
    fi
    sleep 1
  done
}

wait_for http://127.0.0.1:5180/api/health "後端" 120
wait_for http://127.0.0.1:9180/ "前端" 120
echo "已就緒：http://127.0.0.1:9180/"
[ -z "$NO_OPEN" ] && open http://127.0.0.1:9180/
wait
