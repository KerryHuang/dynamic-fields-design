// Vue Flow build 守門（fg3 J69 第 2 層）：反向檢查，不必列舉入口塊。
// 含 Vue Flow（vue-flow__pane）的塊，沿「靜態 import」往回找 importer：每個 importer 都必須是
// 設計頁（FlowDesignerPage-*）、進度圖畫布（ProgressGraphCanvas-*），或只被這些塊靜態 import 的共用塊。
// 沒有任何靜態 importer 又不在允許清單的塊（入口、路由塊、被 eager glob 拉進去的塊都會落在這裡）＝違規。
// 動態 import("./x") 不算（`__vite__mapDeps` 預載清單只是檔名字串，也不算）。
// 用法：node scripts/unattended/vue-flow-chunks.js [assets 目錄，預設 frontend/dist/spa/assets]；違反時列出路徑鏈並以 1 結束。
const fs = require('node:fs');
const path = require('node:path');

const dir = path.resolve(process.argv[2] ?? path.join(__dirname, '../../frontend/dist/spa/assets'));
const MARKER = 'vue-flow__pane';
const ALLOWED = /^(FlowDesignerPage|ProgressGraphCanvas)-.+\.js$/;
const STATIC_IMPORT = /(?:from|import)\s*["']\.\/([^"']+\.js)["']/g;

if (!fs.existsSync(dir)) { console.error(`找不到 ${dir}，請先 bun run build`); process.exit(2); }
const chunks = fs.readdirSync(dir).filter((f) => f.endsWith('.js'));
const text = new Map(chunks.map((f) => [f, fs.readFileSync(path.join(dir, f), 'utf8')]));

const vueFlow = chunks.filter((f) => text.get(f).includes(MARKER));
const allowed = chunks.filter((f) => ALLOWED.test(f));
if (!vueFlow.length) { console.error(`${dir} 裡沒有含 Vue Flow 的塊（找不到 ${MARKER}），檢查腳本的比對規則`); process.exit(2); }
if (allowed.length < 2) { console.error('找不到 FlowDesignerPage-* 與 ProgressGraphCanvas-*，檢查腳本的比對規則'); process.exit(2); }

// 靜態 import 的反向表：被 import 的塊 → importer 們
const importers = new Map(chunks.map((f) => [f, new Set()]));
for (const f of chunks) for (const m of text.get(f).matchAll(STATIC_IMPORT)) importers.get(m[1])?.add(f);

/**
 * 某塊是否只被允許的塊（遞移）靜態 import；違規時回傳從該塊往上的一條路徑（含自己），合規回 null。
 * 允許的塊本身沒有靜態 importer 是正常的（路由動態載入）；但若有，importer 一樣要合規（例如別的塊 eager 載入設計頁）。
 */
const verdict = new Map();
function violation(file) {
  if (verdict.has(file)) return verdict.get(file);
  verdict.set(file, null); // 環：先當合規，由環外的證據決定
  const up = [...importers.get(file)];
  let result = up.length || ALLOWED.test(file) ? null : [file];
  for (const u of up) {
    const v = violation(u);
    if (v) { result = [file, ...v]; break; }
  }
  verdict.set(file, result);
  return result;
}

console.log(`含 Vue Flow 的塊 ${vueFlow.length} 個（${vueFlow.join('、')}）；允許的塊 ${allowed.length} 個`);
const bad = vueFlow.map(violation).filter(Boolean);
if (bad.length) {
  console.error('FAIL：含 Vue Flow 的塊被設計頁、進度圖畫布以外的塊靜態引用（由下往上）：');
  for (const c of bad) console.error(`  ${c.join(' <- ')}`);
  process.exit(1);
}
console.log('OK：含 Vue Flow 的塊只被設計頁與進度圖畫布（及只被它們引用的共用塊）靜態引用');
