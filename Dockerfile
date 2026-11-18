# Demo 部署用：前端建置 + 後端發佈，打包成單一容器（API 與前端同一個站台）。
# 建置內容：repo 根目錄（在根目錄執行 docker build -t dynamic-modules .）。

FROM oven/bun:1.4.0 AS frontend
# 保留 frontend 與 shared 的相對位置：前端以 ../../../../shared 引用前後端共用的 JSON
WORKDIR /work/frontend
COPY frontend/package.json frontend/bun.lock ./
RUN bun install --frozen-lockfile --ignore-scripts
COPY shared/ /work/shared/
COPY frontend/ ./
# 前端版本與後端同一組 build-arg（quasar.config.ts 的 defineEnv；GIT_COMMIT 退回 RENDER_GIT_COMMIT）
ARG APP_VERSION=""
ARG GIT_COMMIT=""
ARG BUILD_TIME=""
ARG RENDER_GIT_COMMIT=""
# source map（hidden）移出要對外提供的 dist/spa，另放 /work/sourcemaps 給維運還原呼叫框（設計 T9）
RUN export BUILD_TIME="${BUILD_TIME:-$(date -u +%Y-%m-%dT%H:%M:%SZ)}" && bunx quasar prepare && bunx quasar build \
    && cp -r dist/spa /work/sourcemaps && find /work/sourcemaps -type f ! -name '*.map' -delete && find dist/spa -name '*.map' -delete

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS backend
WORKDIR /src
COPY backend/ ./
# 版本資訊：GIT_COMMIT 優先，退回 Render 傳入的 RENDER_GIT_COMMIT；沒給的欄位在 /api/version 顯示 dev
ARG APP_VERSION=""
ARG GIT_COMMIT=""
ARG BUILD_TIME=""
ARG RENDER_GIT_COMMIT=""
RUN COMMIT="${GIT_COMMIT:-$RENDER_GIT_COMMIT}"; BUILT="${BUILD_TIME:-$(date -u +%Y-%m-%dT%H:%M:%SZ)}"; \
    dotnet publish src/DynamicModules.WebAPI/DynamicModules.WebAPI.csproj -c Release -o /app \
      -p:AppVersion="$APP_VERSION" -p:GitCommit="$COMMIT" -p:BuildTime="$BUILT"

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=backend /app ./
COPY --from=frontend /work/frontend/dist/spa ./wwwroot
COPY --from=frontend /work/sourcemaps ./sourcemaps
# SQLite 放容器內：免費方案沒有永久磁碟，重啟後回到種子資料
ENV ConnectionStrings__Dynamic="Data Source=/app/data/poc.db" \
    ASPNETCORE_ENVIRONMENT=Production \
    PORT=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "DynamicModules.WebAPI.dll"]
