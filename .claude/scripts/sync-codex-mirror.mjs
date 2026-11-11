#!/usr/bin/env node
// 把 Claude 端的設定鏡射成 Codex 端的副本，以 .claude/ 為唯一事實來源：
//   .claude/skills/       → .agents/skills/       （Codex 只讀 .agents/skills，實測 skill roots）
//   .claude/agents/*.md   → .codex/agents/*.toml  （Codex 的 subagent 格式）
//   .claude/rules/*.md    → .agents/rules/*.md    （補 trigger: always_on，給讀 .agents/rules 的工具）
// 兩個工具的 SessionStart hook 都掛這支，先啟動哪個都會把副本拉回一致。
// 要改 skill／agent 一律改 .claude/ 那份，Codex 端的副本是產出物。
// 專案根目錄：--root <dir> ＞ 環境變數 CLAUDE_PROJECT_DIR ＞ 目前工作目錄。
import { readFileSync, writeFileSync, readdirSync, mkdirSync, rmSync, existsSync } from 'node:fs'
import { join, relative, dirname, resolve } from 'node:path'
import { pathToFileURL } from 'node:url'

// 拆出 frontmatter，回傳 { meta, body }
const split = (raw) => {
  const text = raw.replace(/\r\n/g, '\n') // Windows autocrlf checkout 也要認得 frontmatter
  if (!text.startsWith('---\n')) return { meta: '', body: text }
  const end = text.indexOf('\n---\n', 4)
  if (end === -1) return { meta: '', body: text }
  return { meta: text.slice(4, end), body: text.slice(end + 5) }
}

const walk = (root) => {
  const out = []
  const rec = (dir) => {
    for (const e of readdirSync(dir, { withFileTypes: true })) {
      const p = join(dir, e.name)
      // plugharbor install-project 複製模式的 skill（有 .plugharbor-root）不歸 mirror 管
      if (e.isDirectory() && existsSync(join(p, '.plugharbor-root'))) continue
      if (e.isDirectory()) rec(p)
      else if (e.isFile()) out.push(relative(root, p))
    }
  }
  if (existsSync(root)) rec(root)
  return out
}

export function mirror(src, dest) {
  const srcFiles = walk(src)
  const destFiles = walk(dest)
  const changed = []

  for (const rel of srcFiles) {
    const body = readFileSync(join(src, rel))
    const to = join(dest, rel)
    if (existsSync(to) && readFileSync(to).equals(body)) continue
    mkdirSync(dirname(to), { recursive: true })
    writeFileSync(to, body)
    changed.push(rel)
  }

  // 來源空 = 目錄不存在或 checkout 中途，不是「全部被刪」——直接跳過刪除階段
  if (srcFiles.length === 0) return changed

  for (const rel of destFiles.filter((r) => !srcFiles.includes(r))) {
    rmSync(join(dest, rel))
    changed.push(`-${rel}`)
  }

  // 清掉刪檔後留下的空目錄
  const prune = (dir) => {
    if (!existsSync(dir)) return
    for (const e of readdirSync(dir, { withFileTypes: true })) {
      if (e.isDirectory()) prune(join(dir, e.name))
    }
    if (dir !== dest && readdirSync(dir).length === 0) rmSync(dir, { recursive: true })
  }
  prune(dest)

  return changed
}

// Claude 的 agent（frontmatter + markdown）轉成 Codex 的 agent（TOML）。
// tools／model 是 Claude 專有欄位，Codex 沒有對應概念，刻意不帶過去。
export function toCodexAgent(md) {
  const { meta, body } = split(md)
  const name = meta.match(/^name:\s*(.+)$/m)?.[1].trim()
  const description = meta.match(/^description:\s*(.+)$/m)?.[1].trim()
  if (!name || !description) return null
  const fence = '"'.repeat(3)
  const instructions = body.trim().replace(/\\/g, '\\\\').replace(new RegExp(fence, 'g'), '\\"\\"\\"')
  return [
    `name = ${JSON.stringify(name)}`,
    `description = ${JSON.stringify(description)}`,
    `developer_instructions = ${fence}\n${instructions}${fence}`,
    '',
  ].join('\n')
}


export function mirrorRules(src, dest) {
  const srcFiles = walk(src)
  const destFiles = walk(dest)
  const changed = []

  for (const rel of srcFiles) {
    let body = readFileSync(join(src, rel), 'utf8')
    const { meta, body: text } = split(body)
    
    let newMeta = meta
    if (!newMeta) newMeta = 'trigger: always_on'
    else if (!newMeta.includes('trigger:')) newMeta += '\ntrigger: always_on'
    
    const newBody = '---\n' + newMeta.trim() + '\n---\n' + text
    
    const to = join(dest, rel)
    if (existsSync(to) && readFileSync(to, 'utf8') === newBody) continue
    mkdirSync(dirname(to), { recursive: true })
    writeFileSync(to, newBody)
    changed.push(rel)
  }

  if (srcFiles.length === 0) return changed

  for (const rel of destFiles.filter((r) => !srcFiles.includes(r))) {
    rmSync(join(dest, rel))
    changed.push(`-${rel}`)
  }
  return changed
}

export function projectRoot(argv) {
  const i = argv.indexOf('--root')
  return resolve(i >= 0 && argv[i + 1] ? argv[i + 1] : process.env.CLAUDE_PROJECT_DIR || process.cwd())
}

export function mirrorAgents(src, dest) {
  const changed = []
  // 來源目錄被刪光（最後一支 agent 移走）時仍要清副本，所以不提早 return
  const wanted = new Map()
  for (const name of existsSync(src) ? readdirSync(src).filter((n) => n.endsWith('.md')) : []) {
    const toml = toCodexAgent(readFileSync(join(src, name), 'utf8'))
    if (toml) wanted.set(name.replace(/\.md$/, '.toml'), toml)
  }

  mkdirSync(dest, { recursive: true })
  for (const [name, toml] of wanted) {
    const to = join(dest, name)
    if (existsSync(to) && readFileSync(to, 'utf8') === toml) continue
    writeFileSync(to, toml)
    changed.push(name)
  }
  for (const name of readdirSync(dest).filter((n) => n.endsWith('.toml'))) {
    if (wanted.has(name)) continue
    rmSync(join(dest, name))
    changed.push(`-${name}`)
  }
  return changed
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    const root = projectRoot(process.argv.slice(2))
    const changed = [
      ...mirror(join(root, '.claude', 'skills'), join(root, '.agents', 'skills')),
      ...mirrorRules(join(root, '.claude', 'rules'), join(root, '.agents', 'rules')),
      ...mirrorAgents(join(root, '.claude', 'agents'), join(root, '.codex', 'agents')),
    ]
    if (changed.length > 0) {
      const context = `【Codex 鏡像同步】已從 .claude/ 更新 ${changed.length} 個檔：${changed.join('、')}\n> 這是給 Codex 讀的副本，請一併 commit；要改 skill／agent 一律改 .claude/ 那份。`
      process.stdout.write(JSON.stringify({ hookSpecificOutput: { hookEventName: 'SessionStart', additionalContext: context } }))
    }
  } catch (err) {
    // fail-open：同步失敗不擋 session，但留一行到 stderr（verbose 看得到）
    process.stderr.write(`[sync-codex-mirror] 同步失敗：${err?.message ?? err}\n`)
  }
  process.exitCode = 0 // 不用 process.exit()：pipe stdout 在 macOS／Windows 非同步，硬退會截斷輸出
}
