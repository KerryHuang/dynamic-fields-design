// 錄影環境配方（delivery:demo-video）。欄位說明見 plugin 的 references/script-format.md「錄影環境配方」。
// 站台：後端 publish 到 video/.build/app，前端 build 後放進 wwwroot；資料庫在 video/.data/，每次錄影前重建 Demo 資料。
import { execFileSync } from 'node:child_process'
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync, cpSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const repo = join(here, '..')
const app = join(here, '.build', 'app')
const db = join(here, '.data', 'poc.db')
const run = (cmd, args, cwd, env) => execFileSync(cmd, args, { cwd, env, stdio: ['ignore', 'pipe', 'inherit'] })
const head = () => execFileSync('git', ['rev-parse', 'HEAD'], { cwd: repo, encoding: 'utf8' }).trim()

export default {
  // 站台已是目前 commit 建的就跳過；換了 commit 就整個重建，避免錄到舊版
  build: async () => {
    const stamp = join(app, '.commit')
    if (existsSync(stamp) && readFileSync(stamp, 'utf8') === head()) return
    rmSync(app, { recursive: true, force: true })
    run('dotnet', ['publish', join(repo, 'backend/src/DynamicModules.WebAPI/DynamicModules.WebAPI.csproj'), '-c', 'Release', '-o', app, '--nologo', '-v', 'q'], repo)
    const fe = join(repo, 'frontend')
    run('bun', ['install', '--frozen-lockfile'], fe)
    run('bunx', ['quasar', 'build'], fe)
    cpSync(join(fe, 'dist', 'spa'), join(app, 'wwwroot'), { recursive: true })
    writeFileSync(stamp, head())
  },
  // 只刪資料庫與金鑰（dataDir 裡還有引擎的鎖檔），再產生 Demo 資料
  reset: async ({ dataDir, env }) => {
    mkdirSync(dataDir, { recursive: true })
    for (const f of ['poc.db', 'poc.db-wal', 'poc.db-shm', 'keys']) rmSync(join(dataDir, f), { recursive: true, force: true })
    run('dotnet', ['DynamicModules.WebAPI.dll', '--generate-demo-data'], app, env)
  },
  start: {
    cmd: ['dotnet', 'DynamicModules.WebAPI.dll'],
    cwd: '.build/app',
    portEnv: 'PORT',
    env: { ConnectionStrings__Dynamic: `Data Source=${db}`, ASPNETCORE_ENVIRONMENT: 'Production', Demo__LoginEnabled: 'true' },
    readyPath: '/api/health/ready',
    readyTimeoutMs: 120000,
  },
  ui: { menu: '.q-menu', dialog: '.q-dialog' },
  tts: { provider: 'edge', licensed: false },
}
