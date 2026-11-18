#!/usr/bin/env bash
# 一次產兩個同名 migration：SQLite（DynamicModules.Persistence）與 PostgreSQL（DynamicModules.Persistence.Postgres）。
# 用法：scripts/db/add-migration.sh <名稱>
# 兩次都明確帶 --project 與 --startup-project：EF tools 只在 DbContext 所在專案與 startup 專案找 design-time factory，
# 不帶會找到錯的 factory（PostgreSQL provider 設計 §5.3）。產完跑 MigrationConsistencyTests 確認兩邊都沒有待產生的變更。
set -euo pipefail

if [ $# -ne 1 ] || [ -z "$1" ]; then
  echo "用法：$0 <migration 名稱>" >&2
  exit 2
fi
name="$1"
backend="$(cd "$(dirname "$0")/../../backend" && pwd)"
cd "$backend"

dotnet tool restore >/dev/null
dotnet tool run dotnet-ef migrations add "$name" \
  --project src/DynamicModules.Persistence --startup-project src/DynamicModules.Persistence --output-dir Migrations
dotnet tool run dotnet-ef migrations add "$name" \
  --project src/DynamicModules.Persistence.Postgres --startup-project src/DynamicModules.Persistence.Postgres --output-dir Migrations

echo "已產生 SQLite 與 PostgreSQL 的 migration「${name}」；請跑："
echo "  dotnet test tests/DynamicModules.ApplicationTests --filter \"FullyQualifiedName~MigrationConsistencyTests\""
