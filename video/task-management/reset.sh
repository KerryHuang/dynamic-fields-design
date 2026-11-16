#!/bin/zsh
# 停掉自己起的 server、清掉資料庫、重新產生 Demo 資料，在 5191 啟動（沿用 .build/app）。
# 5180 會被其他 session 的開發 server 佔用，所以用 PORT 指定埠；就緒判斷要求聽 5191 的程序就是自己起的。
cd "${0:A:h}"
[ -f server.pid ] && kill $(cat server.pid) 2>/dev/null; sleep 2
lsof -tiTCP:5191 -sTCP:LISTEN >/dev/null && { echo "5191 已被占用，停止"; exit 1; }
rm -rf data; mkdir -p data
export ConnectionStrings__Dynamic="Data Source=$PWD/data/poc.db" ASPNETCORE_ENVIRONMENT=Production Demo__LoginEnabled=true Attachments__UploadEnabled=true PORT=5191
(cd .build/app && dotnet DynamicModules.WebAPI.dll --generate-demo-data >../../seed.log 2>&1) || { echo "seed failed"; exit 1; }
(cd .build/app && exec dotnet DynamicModules.WebAPI.dll > ../../server.log 2>&1) &
echo $! > server.pid
for i in {1..60}; do
  [ "$(lsof -tiTCP:5191 -sTCP:LISTEN)" = "$(cat server.pid)" ] && curl -s -m 1 -o /dev/null http://127.0.0.1:5191/api/health/ready && { echo "ready pid $(cat server.pid) on 5191"; exit 0; }
  sleep 0.5; done
echo "not ready"; exit 1
