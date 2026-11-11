#!/usr/bin/env node
// 只給 Codex 用的規則注入。
// Claude Code 會自動載入 user 層 CLAUDE.md／shared／rules 與 workspace 的 .claude/rules；
// Codex 只讀 AGENTS.md，其餘一概看不到（實測 codex debug prompt-input）。
// 這支在 Codex 的 SessionStart 把「Claude 會自動載入的那一組」原樣讀出來注入，
// 讓兩邊吃同一份來源檔，不另抄副本。
// 專案根目錄：--root <dir> ＞ 環境變數 CLAUDE_PROJECT_DIR ＞ 目前工作目錄。
// --no-user：user 層規則已由別的機制（如生成的 ~/.codex/AGENTS.md）載入時，只注入專案層，避免重複。
import { readFileSync, readdirSync, existsSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { homedir } from 'node:os'
import { pathToFileURL } from 'node:url'

const read = (p) => (existsSync(p) ? readFileSync(p, 'utf8') : null)

// 拆出 frontmatter，回傳 { meta, body }
const split = (raw) => {
  const text = raw.replace(/\r\n/g, '\n') // Windows autocrlf checkout 也要認得 frontmatter
  if (!text.startsWith('---\n')) return { meta: '', body: text }
  const end = text.indexOf('\n---\n', 4)
  if (end === -1) return { meta: '', body: text }
  return { meta: text.slice(4, end), body: text.slice(end + 5) }
}

// 展開 user CLAUDE.md 的 @shared/xxx.md 匯入
const expandImports = (text, base) =>
  text.replace(/^@(\S+\.md)\s*$/gm, (m, rel) => read(join(base, rel))?.trim() ?? m)

export function collect(homeClaude, wsClaude, { includeUser = true } = {}) {
  const blocks = []
  const scoped = []

  const userRoot = read(join(homeClaude, 'CLAUDE.md'))
  if (includeUser && userRoot) blocks.push(['user 全域行為（~/.claude/CLAUDE.md）', expandImports(userRoot, homeClaude)])

  for (const dir of [...(includeUser ? [join(homeClaude, 'rules')] : []), join(wsClaude, 'rules')]) {
    if (!existsSync(dir)) continue
    const label = dir.startsWith(homeClaude) ? '~/.claude/rules' : '.claude/rules'
    for (const name of readdirSync(dir).filter((n) => n.endsWith('.md')).sort()) {
      const { meta, body } = split(readFileSync(join(dir, name), 'utf8'))
      if (/^paths:/m.test(meta)) {
        const globs = meta.match(/^\s*-\s*"?([^"\n]+)"?\s*$/gm)?.map((l) => l.replace(/^\s*-\s*"?|"?\s*$/g, '')) ?? []
        scoped.push(`- \`${label}/${name}\` — 動到 ${globs.join('、')} 之前必讀`)
      } else {
        blocks.push([`${label}/${name}`, body.trim()])
      }
    }
  }

  const out = ['以下是 Claude Code 會自動載入、但 Codex 不會的規則。本 session 全程適用，與 AGENTS.md 同級。']
  for (const [title, body] of blocks) out.push(`\n## ${title}\n\n${body}`)
  if (scoped.length > 0) {
    out.push(`\n## 條件式規則（未內含，要用時自己去讀）\n\n${scoped.join('\n')}`)
  }
  return out.join('\n')
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    const argv = process.argv.slice(2)
    const i = argv.indexOf('--root')
    const root = resolve(i >= 0 && argv[i + 1] ? argv[i + 1] : process.env.CLAUDE_PROJECT_DIR || process.cwd())
    const context = collect(join(homedir(), '.claude'), join(root, '.claude'), { includeUser: !argv.includes('--no-user') })
    process.stdout.write(JSON.stringify({ hookSpecificOutput: { hookEventName: 'SessionStart', additionalContext: context } }))
  } catch {
    // fail-open：讀不到規則不擋 session
  }
  process.exitCode = 0 // 不用 process.exit()：pipe stdout 在 macOS／Windows 非同步，硬退會截斷輸出
}
