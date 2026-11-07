import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

const DIR = resolve(process.cwd(), 'src/css/themes');
const STYLES = ['workbench', 'airy', 'enterprise', 'industrial'] as const;
const MODES = ['light', 'dark', 'system'] as const;
const read = (f: string) => readFileSync(resolve(DIR, f), 'utf8');

type Tokens = Record<string, string>;

/** 把 `selector { --a: x; }` 解析成 selector → token；不處理巢狀。 */
function parseBlocks(css: string): Map<string, Tokens> {
  const out = new Map<string, Tokens>();
  const clean = css.replace(/\/\*[\s\S]*?\*\//g, '');
  for (const m of clean.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    const tokens: Tokens = {};
    for (const d of m[2].matchAll(/(--[\w-]+)\s*:\s*([^;]+);/g)) tokens[d[1]] = d[2].trim();
    for (const sel of m[1].split(',').map((s) => s.trim().replace(/"/g, "'"))) {
      out.set(sel, { ...(out.get(sel) ?? {}), ...tokens });
    }
  }
  return out;
}

const shared = parseBlocks(read('tokens.css'));
const files = Object.fromEntries(STYLES.map((s) => [s, parseBlocks(read(`${s}.css`))]));

function palette(style: (typeof STYLES)[number], mode: 'light' | 'dark'): Tokens {
  return {
    ...shared.get(`[data-mode='${mode}']`),
    ...files[style].get(`[data-style='${style}']`),
    ...files[style].get(`[data-style='${style}'][data-mode='${mode}']`),
  };
}

function luminance(hex: string): number {
  const n = parseInt(hex.slice(1), 16);
  const [r, g, b] = [(n >> 16) & 255, (n >> 8) & 255, n & 255].map((v) => {
    const c = v / 255;
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}
function contrast(a: string, b: string): number {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
}

const AA: [string, string][] = [
  ['--t-text', '--t-bg'], ['--t-text', '--t-panel'], ['--t-muted', '--t-bg'], ['--t-muted', '--t-panel'],
  ['--t-primary-text', '--t-bg'], ['--t-primary-text', '--t-panel'], ['--t-on-primary', '--t-primary'],
  ['--t-negative', '--t-bg'], ['--t-negative', '--t-panel'], ['--t-on-negative', '--t-negative'],
  ['--t-topbar-fg', '--t-topbar-bg'], ['--t-sidebar-fg', '--t-sidebar-bg'], ['--t-sidebar-muted', '--t-sidebar-bg'],
  ['--t-selected-fg', '--t-selected-bg'], ['--t-positive', '--t-panel'], ['--t-warning', '--t-bg'], ['--t-warning', '--t-panel'],
  ['--t-info', '--t-panel'], ['--t-on-positive', '--t-positive'], ['--t-on-warning', '--t-warning'], ['--t-on-info', '--t-info'],
  ['--t-text', '--t-negative-soft'], ['--t-text', '--t-info-soft'],
];
const LARGE = ['--t-chart-1', '--t-chart-2', '--t-chart-3', '--t-chart-4', '--t-chart-5', '--t-chart-6'];

const LAYOUT = new Set([
  '--t-font-family', '--t-font-size', '--t-radius', '--t-panel-padding', '--t-cell-padding', '--t-topbar-height', '--t-th-weight',
  '--t-th-border-bottom', '--t-selected-marker', '--t-title-size', '--t-stat-size', '--t-stat-weight', '--t-card-border', '--t-card-shadow',
  '--t-space-xs', '--t-space-sm', '--t-space-md', '--t-space-lg', '--t-space-xl', '--t-field-height', '--t-row-height',
]);
const combinations = STYLES.flatMap((s) => MODES.map((m) => [s, m] as const));
/** system 在瀏覽器偏好淺色與深色時各解析成一種 palette，兩種都要過。 */
const palettesOf = (s: (typeof STYLES)[number], m: (typeof MODES)[number]) =>
  (m === 'system' ? (['light', 'dark'] as const) : [m]).map((x) => palette(s, x));

describe('主題 token', () => {
  it('有 4 種風格 × 3 種模式共 12 種組合', () => {
    expect(combinations).toHaveLength(12);
  });

  it.each(combinations)('%s %s 內文對比至少 4.5', (style, mode) => {
    for (const p of palettesOf(style, mode)) {
      for (const [fg, bg] of AA) {
        expect(p[fg], `${style} 缺 ${fg}`).toMatch(/^#[0-9a-f]{6}$/i);
        expect(p[bg], `${style} 缺 ${bg}`).toMatch(/^#[0-9a-f]{6}$/i);
        expect(contrast(p[fg], p[bg]), `${style} ${fg} on ${bg}`).toBeGreaterThanOrEqual(4.5);
      }
    }
  });

  it.each(combinations)('%s %s 圖表色對比至少 3', (style, mode) => {
    for (const p of palettesOf(style, mode)) {
      for (const t of LARGE) {
        for (const bg of ['--t-bg', '--t-panel']) {
          expect(contrast(p[t], p[bg]), `${style} ${t} on ${bg}`).toBeGreaterThanOrEqual(3);
        }
      }
    }
  });

  it('所有風格檔都定義了相同的 token 集合', () => {
    // airy 的框線與陰影隨模式改變，寫在模式區塊，所以比較合併後的結果
    const keys = (s: (typeof STYLES)[number], m: 'light' | 'dark') => Object.keys(palette(s, m)).sort();
    const first = keys('workbench', 'light');
    expect(first).toHaveLength(52);
    expect(keys('workbench', 'dark')).toEqual(first);
    for (const s of STYLES) for (const m of ['light', 'dark'] as const) expect(keys(s, m)).toEqual(first);
  });

  it(':root 提供 workbench 淺色的同一組值，內嵌 script 沒跑時也有樣式', () => {
    const root = shared.get(':root') ?? {};
    const expected = { ...palette('workbench', 'light'), ...files.workbench.get("[data-style='workbench']") };
    expect(root).toEqual(expected);
  });

  it('密度：每種風格的間距都比 Quasar 預設（md 16px）緊，現場工業的欄位高度維持觸控尺寸', () => {
    const px = (v: string) => Number.parseFloat(v);
    for (const s of STYLES) {
      const t = files[s].get(`[data-style='${s}']`) ?? {};
      const steps = ['xs', 'sm', 'md', 'lg', 'xl'].map((k) => px(t[`--t-space-${k}`]));
      expect(steps.every((n) => Number.isFinite(n)), `${s} 缺間距 token`).toBe(true);
      expect(steps, `${s} 間距要由小到大`).toEqual([...steps].sort((a, b) => a - b));
      expect(px(t['--t-space-md']), `${s} md 間距`).toBeLessThanOrEqual(12);
      expect(px(t['--t-space-sm']), `${s} sm 間距`).toBeLessThanOrEqual(6);
    }
    expect(px(files.industrial.get("[data-style='industrial']")!['--t-field-height'])).toBeGreaterThanOrEqual(44);
    const workbench = px(files.workbench.get("[data-style='workbench']")!['--t-space-md']);
    for (const s of STYLES) {
      expect(px(files[s].get(`[data-style='${s}']`)!['--t-space-md']), `${s} 不得比工作台更緊`).toBeGreaterThanOrEqual(workbench);
    }
  });

  it('CSS 不引用外部字型或資源', () => {
    for (const f of ['tokens.css', ...STYLES.map((s) => `${s}.css`)]) {
      expect(read(f)).not.toMatch(/@import|url\(\s*['"]?https?:|fonts\.googleapis|@font-face/);
    }
  });

  it('只用系統字型堆疊', () => {
    for (const s of STYLES) {
      const ff = files[s].get(`[data-style='${s}']`)?.['--t-font-family'] ?? '';
      expect(ff).toContain('system-ui');
      expect(ff).toContain("'Microsoft JhengHei'");
      expect(ff.endsWith('sans-serif')).toBe(true);
    }
  });

  it('顏色一律寫十六進位字面值', () => {
    for (const s of STYLES) {
      for (const mode of ['light', 'dark'] as const) {
        const p = palette(s, mode);
        for (const [k, v] of Object.entries(p)) {
          if (LAYOUT.has(k)) continue;
          expect(v, `${s} ${mode} ${k}`).toMatch(/^#[0-9a-f]{6}$/i);
        }
      }
    }
  });
});
