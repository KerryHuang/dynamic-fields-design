import { readdirSync, readFileSync } from 'node:fs';
import { basename, dirname, join, relative, resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

// Vue Flow 動態載入邊界（fg3 J69 第 1 層）：runtime 引用 @vue-flow 只允許出現在下面這份清單，
// 清單內的檔案只被清單、設計頁、進度圖的 defineAsyncComponent 引用。之後加檔案要改這份清單，審查看得到。
const ALLOWED = [
  'presentation/dynamic/approval/ProgressGraphCanvas.vue',
  'presentation/dynamic/approval/ProgressGraphNode.vue',
  'presentation/dynamic/approval-designer/graph/DesignerCanvas.vue',
  'presentation/dynamic/approval-designer/graph/DesignerNode.vue',
  'presentation/dynamic/approval-designer/graph/DesignerEdge.vue',
];
const DESIGNER_PAGE = 'pages/system/dynamic-modules/FlowDesignerPage.vue';
const PROGRESS_HOST = 'presentation/dynamic/approval/ApprovalProgressGraph.vue';
const ROUTES = 'router/routes.ts';

const SRC = resolve(import.meta.dirname, '../..');
const files: string[] = [];
(function walk(dir: string) {
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    const p = join(dir, e.name);
    if (e.isDirectory()) walk(p);
    else if (/\.(ts|vue)$/.test(e.name) && !/\.spec\.ts$/.test(e.name)) files.push(relative(SRC, p));
  }
})(SRC);
const read = (f: string) => readFileSync(join(SRC, f), 'utf8');

/** 整句 `import type … from '@vue-flow/…'` 不算 runtime；行內 type、副作用 import、動態 import 都算。 */
export function hasRuntimeVueFlow(source: string): boolean {
  const withoutTypeImports = source.replace(/^\s*import\s+type\s[^;]*?from\s+['"]@vue-flow\/[^'"]+['"];?/gm, '');
  return /['"]@vue-flow\/[^'"]+['"]/.test(withoutTypeImports);
}
/** 引用某個檔案的 import（靜態 `from '…/X.vue'`、副作用 `import '…/X.vue'`、動態 `import('…/X.vue')`）。 */
export function importsOf(source: string, name: string): { dynamic: boolean }[] {
  const out: { dynamic: boolean }[] = [];
  const esc = name.replace(/\./g, '\\.');
  for (const m of source.matchAll(new RegExp(`(from\\s+|import\\s*\\(?\\s*)['"][^'"]*[/']${esc}['"]`, 'g'))) out.push({ dynamic: /import\s*\(/.test(m[0]) });
  return out;
}

/** `import.meta.glob(…)` 的 pattern 字串（第一個參數，字串或字串陣列）。 */
export function globPatternsOf(source: string): string[] {
  const out: string[] = [];
  for (const m of source.matchAll(/import\.meta\.glob\(\s*(\[[^\]]*\]|'[^']*'|"[^"]*"|`[^`]*`)/g)) {
    for (const s of m[1].matchAll(/['"`]([^'"`]+)['"`]/g)) out.push(s[1]);
  }
  return out;
}
/** 簡化的 glob → RegExp：`**`、`*`、`?`、`{a,b}`；夠用來判斷 pattern 有沒有涵蓋某個檔案。 */
export function globToRegExp(glob: string): RegExp {
  let re = '';
  for (let i = 0; i < glob.length; i++) {
    const c = glob[i];
    if (c === '*') { if (glob[i + 1] === '*') { re += '.*'; i++; if (glob[i + 1] === '/') i++; } else re += '[^/]*'; }
    else if (c === '?') re += '[^/]';
    else if (c === '{') re += '(?:';
    else if (c === '}') re += ')';
    else if (c === ',') re += /\{[^}]*$/.test(glob.slice(0, i)) ? '|' : ',';
    else re += c.replace(/[.+^$()|[\]\\]/g, '\\$&');
  }
  return new RegExp(`^${re}$`);
}

describe('hasRuntimeVueFlow', () => {
  it.each([
    ["import { VueFlow } from '@vue-flow/core';", true],
    ["import { type Node } from '@vue-flow/core';", true],
    ["import '@vue-flow/core/dist/style.css';", true],
    ["const m = await import('@vue-flow/core');", true],
    ["import type { Node, Edge } from '@vue-flow/core';", false],
    ["import type {\n  Node,\n  Edge,\n} from '@vue-flow/core'", false],
    ["import { ref } from 'vue';", false],
  ])('%s', (src, expected) => { expect(hasRuntimeVueFlow(src)).toBe(expected); });
});

describe('globPatternsOf and globToRegExp', () => {
  it('reads string and array arguments', () => {
    expect(globPatternsOf("const a = import.meta.glob('./x/*.vue', { eager: true });")).toEqual(['./x/*.vue']);
    expect(globPatternsOf('import.meta.glob(["./a/*.ts", "!./a/b.ts"])')).toEqual(['./a/*.ts', '!./a/b.ts']);
    expect(globPatternsOf("import { ref } from 'vue';")).toEqual([]);
  });
  it.each([
    ['approval-designer/graph/*.vue', 'approval-designer/graph/DesignerCanvas.vue', true],
    ['approval-designer/**/*.vue', 'approval-designer/graph/DesignerCanvas.vue', true],
    ['**/Designer{Canvas,Node}.vue', 'approval-designer/graph/DesignerNode.vue', true],
    ['approval-designer/*.vue', 'approval-designer/graph/DesignerCanvas.vue', false],
    ['pages/**/*.vue', 'approval-designer/graph/DesignerCanvas.vue', false],
  ])('%s vs %s', (glob, file, expected) => { expect(globToRegExp(glob).test(file)).toBe(expected); });
});

describe('vue flow boundary', () => {
  it('only the allowlisted files import @vue-flow at runtime, inline type imports count', () => {
    const offenders = files.filter((f) => hasRuntimeVueFlow(read(f)) && !ALLOWED.includes(f));
    expect(offenders).toEqual([]);
  });

  it('every allowlisted file exists (a rename must update the list)', () => {
    for (const f of ALLOWED) expect(files).toContain(f);
  });

  it('allowlisted designer files are only imported by the designer page', () => {
    for (const f of ALLOWED.filter((x) => x.includes('approval-designer/graph/'))) {
      const importers = files.filter((o) => o !== f && importsOf(read(o), basename(f)).length > 0);
      for (const o of importers) expect([...ALLOWED, DESIGNER_PAGE], `${o} imports ${f}`).toContain(o);
    }
  });

  it('the progress canvas and node are only reached from the allowlist or the host, and the host only dynamically', () => {
    for (const f of ALLOWED.filter((x) => x.includes('approval/Progress'))) {
      for (const o of files.filter((x) => x !== f)) {
        const hits = importsOf(read(o), basename(f));
        if (!hits.length) continue;
        expect([...ALLOWED, PROGRESS_HOST], `${o} imports ${f}`).toContain(o);
        // 宿主（詳情頁塊）對清單內任何檔案的引用都必須是動態 import：靜態引用 Node 一樣會把 Vue Flow 拉進詳情頁
        if (o === PROGRESS_HOST) expect(hits.every((h) => h.dynamic), `${o} must import ${f} dynamically`).toBe(true);
      }
    }
  });

  it('no import.meta.glob pattern covers an allowlisted file, the designer page or the designer graph folder (an eager glob would bypass the import checks)', () => {
    const guarded = [...new Set([...ALLOWED, DESIGNER_PAGE, ...files.filter((f) => f.startsWith('presentation/dynamic/approval-designer/graph/'))])];
    const hits: string[] = [];
    for (const f of files) {
      for (const raw of globPatternsOf(read(f))) {
        if (raw.startsWith('!')) continue;
        // pattern 相對於引用它的檔案；'/…' 是專案根（frontend/），其餘（含 'src/…'）當作專案根下
        const abs = raw.startsWith('.') ? resolve(dirname(join(SRC, f)), raw) : resolve(SRC, '..', raw.replace(/^\//, ''));
        const rel = relative(SRC, abs);
        for (const g of guarded) if (globToRegExp(rel).test(g)) hits.push(`${f}: ${raw} covers ${g}`);
      }
    }
    expect(hits).toEqual([]);
  });

  it('the designer page is only reached through a lazy route', () => {
    const importers = files.filter((o) => importsOf(read(o), 'FlowDesignerPage.vue').length > 0);
    expect(importers).toEqual([ROUTES]);
    expect(importsOf(read(ROUTES), 'FlowDesignerPage.vue').every((h) => h.dynamic)).toBe(true);
  });

  it('the flow list tab only navigates; it imports nothing from the designer graph folder', () => {
    expect(read('presentation/dynamic/approval-designer/ApprovalFlowsTab.vue')).not.toMatch(/approval-designer\/graph|FlowDesignerPage/);
  });

  it('the designer composable and graph mapper never call useVueFlow', () => {
    for (const f of ['use-graph-designer.ts', 'use-designer-actions.ts', 'designer-graph.ts'].map((n) => `presentation/dynamic/approval-designer/graph/${n}`)) {
      expect(read(f), f).not.toContain('useVueFlow');
    }
  });
});
