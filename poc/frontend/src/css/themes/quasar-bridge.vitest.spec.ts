import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

const read = (f: string) => readFileSync(resolve(process.cwd(), 'src/css/themes', f), 'utf8').replace(/\/\*[\s\S]*?\*\//g, '');
const bridge = read('quasar-bridge.css');
const print = read('print.css');

/** 回傳選擇器清單含 sel 的所有規則內容（不處理巢狀 @media 以外的結構）。 */
function bodiesOf(css: string, sel: string): string[] {
  const out: string[] = [];
  for (const m of css.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    if (m[1].split(',').map((s) => s.trim()).includes(sel)) out.push(m[2]);
  }
  return out;
}

describe('quasar-bridge.css', () => {
  it.each([
    ['.text-primary', 'color', '--t-primary-text'],
    ['.bg-primary.text-white', 'color', '--t-on-primary'],
    ['.bg-negative.text-white', 'color', '--t-on-negative'],
    ['.bg-positive.text-white', 'color', '--t-on-positive'],
    ['.bg-warning.text-white', 'color', '--t-on-warning'],
    ['.bg-warning.text-dark', 'color', '--t-on-warning'],
    ['.bg-info.text-white', 'color', '--t-on-info'],
    ['.text-muted', 'color', '--t-muted'],
  ])('%s 用 %s 且帶 !important', (sel, prop, token) => {
    const bodies = bodiesOf(bridge, sel).join(';');
    expect(bodies).toMatch(new RegExp(`${prop}:\\s*var\\(${token}\\)\\s*!important`));
  });

  it('--q-primary 與 --q-dark 接到 token', () => {
    expect(bridge).toMatch(/--q-primary:\s*var\(--t-primary\)/);
    expect(bridge).toMatch(/--q-negative:\s*var\(--t-negative\)/);
    expect(bridge).toMatch(/--q-dark:\s*var\(--t-panel\)/);
    expect(bridge).toMatch(/--q-dark-page:\s*var\(--t-bg\)/);
  });

  it('提供 Task 7 與小樣本使用的 class', () => {
    for (const sel of ['.bg-muted', '.bg-negative-soft', '.bg-info-soft', '.text-stat', '.theme-sample']) {
      expect(bodiesOf(bridge, sel).length, sel).toBeGreaterThan(0);
    }
    expect(bodiesOf(bridge, '.text-stat').join(';')).toContain('tabular-nums');
  });

  it('q-table 密度連 dense 一起覆蓋', () => {
    expect(bridge).toContain('.q-table--dense');
    expect(bodiesOf(bridge, '[data-style] .q-table th').join(';')).toContain('--t-th-weight');
  });

  it('有 :focus-visible 樣式，頂列與邊欄各用自己的前景色', () => {
    expect(bodiesOf(bridge, ':focus-visible').join(';')).toMatch(/outline:\s*2px solid var\(--t-text\)/);
    expect(bodiesOf(bridge, '.q-header :focus-visible').join(';')).toContain('--t-topbar-fg');
    expect(bodiesOf(bridge, '.q-drawer :focus-visible').join(';')).toContain('--t-sidebar-fg');
  });

  it('html 上有 color-scheme', () => {
    expect(bodiesOf(bridge, "[data-mode='dark']").join(';')).toContain('color-scheme: dark');
    expect(bodiesOf(bridge, "[data-mode='light']").join(';')).toContain('color-scheme: light');
  });

  it('bridge 沒有顏色字面值', () => {
    expect(bridge).not.toMatch(/#[0-9a-f]{3,8}\b/i);
    expect(bridge).not.toMatch(/\b(rgb|rgba|hsl|hsla)\(/i);
    expect(bridge).not.toMatch(/:\s*(white|black|red|grey|gray)\b/i);
  });
});

describe('print.css', () => {
  it('在 @media print 內強制白底黑字', () => {
    expect(print).toContain('@media print');
    const body = bodiesOf(print, 'body').join(';');
    expect(body).toMatch(/background:\s*#fff/);
    expect(body).toMatch(/color:\s*#000/);
    for (const t of ['--t-bg', '--t-panel', '--t-text', '--t-topbar-bg', '--t-sidebar-bg']) expect(print).toContain(t);
  });
});
