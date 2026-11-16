// Vue Flow build 守門（fg3 J69 第 2 層）：從入口塊 index-*、DetailPage-*、ModuleEditorPage-* 沿靜態 import 遞移展開，
// 展開到的每個塊都不得含 Vue Flow（vue-flow__pane）。動態 import("./x") 不展開。
// 用法：node scripts/unattended/vue-flow-chunks.js [assets 目錄，預設 frontend/dist/spa/assets]；違反時列出路徑鏈並以 1 結束。
const fs = require('node:fs');
const path = require('node:path');

const dir = path.resolve(process.argv[2] ?? path.join(__dirname, '../../frontend/dist/spa/assets'));
const MARKER = 'vue-flow__pane';
const ENTRY = /^(index|DetailPage|ModuleEditorPage)-.+\.js$/;
const STATIC_IMPORT = /(?:from|import)\s*["']\.\/([^"']+\.js)["']/g;

if (!fs.existsSync(dir)) { console.error(`找不到 ${dir}，請先 bun run build`); process.exit(2); }
const chunks = fs.readdirSync(dir).filter((f) => f.endsWith('.js'));
const entries = chunks.filter((f) => ENTRY.test(f));
if (!entries.length) { console.error(`${dir} 裡沒有入口塊（index-*、DetailPage-*、ModuleEditorPage-*）`); process.exit(2); }
if (!entries.some((f) => f.startsWith('index-')) || !entries.some((f) => f.startsWith('ModuleEditorPage-'))) {
  console.error('入口塊不齊（至少要有 index-* 與 ModuleEditorPage-*），檢查腳本的比對規則');
  process.exit(2);
}

const text = new Map();
const read = (f) => {
  if (!text.has(f)) text.set(f, fs.existsSync(path.join(dir, f)) ? fs.readFileSync(path.join(dir, f), 'utf8') : '');
  return text.get(f);
};

const bad = [];
const seen = new Set();
function walk(file, chain) {
  if (seen.has(file)) return;
  seen.add(file);
  const src = read(file);
  const here = [...chain, file];
  if (src.includes(MARKER)) bad.push(here);
  for (const m of src.matchAll(STATIC_IMPORT)) walk(m[1], here);
}
for (const e of entries) walk(e, []);

console.log(`入口塊 ${entries.length} 個、遞移展開 ${seen.size} 個塊`);
if (bad.length) {
  console.error('FAIL：入口塊的靜態閉包含 Vue Flow');
  for (const c of bad) console.error(`  ${c.join(' -> ')}`);
  process.exit(1);
}
console.log('OK：入口塊的靜態閉包不含 Vue Flow');
