# Demo 部署用：前端建置 + 後端發佈，打包成單一容器（API 與前端同一個站台）。
# 建置內容：poc/（在 poc/ 目錄執行 docker build）。

FROM oven/bun:1.4.0 AS frontend
# 保留 poc/ 的相對位置：前端以 ../../../../shared 引用前後端共用的 JSON
WORKDIR /poc/frontend
COPY frontend/package.json frontend/bun.lock ./
RUN bun install --frozen-lockfile --ignore-scripts
COPY shared/ /poc/shared/
COPY frontend/ ./
RUN bunx quasar prepare && bunx quasar build

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
COPY --from=frontend /poc/frontend/dist/spa ./wwwroot
# SQLite 放容器內：免費方案沒有永久磁碟，重啟後回到種子資料
ENV ConnectionStrings__Dynamic="Data Source=/app/data/poc.db" \
    ASPNETCORE_ENVIRONMENT=Production \
    PORT=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "DynamicModules.WebAPI.dll"]
