// 產生 shared/quasar-langs.json：列出 frontend/node_modules/quasar/lang/*.js 的語言包名稱（排序、一行一筆）。
// 手動執行（升級 Quasar 後 quasar-langs.vitest.spec.ts 失敗時）：node scripts/i18n/gen-quasar-langs.mjs
// 以腳本自身位置找路徑，從任何目錄執行都可以；後端內嵌這份清單驗語系的 quasarLang（語系包設計 §8.5）。
import { readdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const langDir = join(root, 'frontend', 'node_modules', 'quasar', 'lang');
const langs = readdirSync(langDir)
  .filter((f) => f.endsWith('.js'))
  .map((f) => f.slice(0, -'.js'.length))
  .sort();
const out = join(root, 'shared', 'quasar-langs.json');
writeFileSync(out, `[\n${langs.map((l) => `  ${JSON.stringify(l)}`).join(',\n')}\n]\n`);
console.log(`寫入 ${langs.length} 個語言包到 shared/quasar-langs.json`);
