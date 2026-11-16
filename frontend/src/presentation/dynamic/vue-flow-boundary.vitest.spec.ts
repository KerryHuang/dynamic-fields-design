import { readdirSync, readFileSync } from 'node:fs';
import { basename, join, relative, resolve } from 'node:path';
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

  it('the progress canvas is only reached through defineAsyncComponent', () => {
    for (const f of ALLOWED.filter((x) => x.includes('approval/Progress'))) {
      for (const o of files.filter((x) => x !== f)) {
        const hits = importsOf(read(o), basename(f));
        if (!hits.length) continue;
        expect([...ALLOWED, PROGRESS_HOST], `${o} imports ${f}`).toContain(o);
        if (o === PROGRESS_HOST && basename(f) === 'ProgressGraphCanvas.vue') expect(hits.every((h) => h.dynamic)).toBe(true);
      }
    }
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
    for (const f of ['presentation/dynamic/approval-designer/graph/use-graph-designer.ts', 'presentation/dynamic/approval-designer/graph/designer-graph.ts']) {
      expect(read(f), f).not.toContain('useVueFlow');
    }
  });
});
