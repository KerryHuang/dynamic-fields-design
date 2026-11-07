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

  it('邊欄選取項的前景排在一般作用中文字之後，否則 industrial 的選取底（主色）會配上主色文字', () => {
    const generic = bridge.indexOf('[data-style] .q-item--active');
    const drawer = bridge.indexOf('[data-style] .q-drawer .q-router-link--active');
    expect(generic).toBeGreaterThan(-1);
    expect(drawer).toBeGreaterThan(generic);
  });

  it('Quasar 的 .no-outline（q-btn 等都帶）不能吃掉鍵盤焦點框，頂欄與側欄用各自的前景色', () => {
    const all = (sel: string) => bodiesOf(bridge, sel).join(';');
    expect(all('.no-outline:focus-visible')).toMatch(/outline:\s*2px solid var\(--t-text\)\s*!important/);
    expect(all('.q-header .no-outline:focus-visible')).toMatch(/outline-color:\s*var\(--t-topbar-fg\)\s*!important/);
    expect(all('.q-drawer .no-outline:focus-visible')).toMatch(/outline-color:\s*var\(--t-sidebar-fg\)\s*!important/);
  });

  it('欄位內的清除圖示（q-field__focusable-action）自己有焦點框，不吃 Quasar 的 outline: 0', () => {
    expect(bodiesOf(bridge, '.q-field__focusable-action:focus-visible').join(';')).toMatch(/outline:\s*2px solid var\(--t-text\)\s*!important/);
  });

  it('單獨的 text-dark 用主文字色，不跟著 --q-dark 變成面板色', () => {
    expect(bodiesOf(bridge, '.text-dark').join(';')).toMatch(/color:\s*var\(--t-text\)\s*!important/);
  });

  it('outline 的 q-badge 文字與框線用 primary-text，不能沿用實心底的 on-primary', () => {
    expect(bodiesOf(bridge, '.q-badge--outline').join(';')).toContain('var(--t-primary-text)');
  });

  it('欄位內可聚焦的按鈕（如 clearable）保留焦點框，只有輸入元素關掉', () => {
    expect(bodiesOf(bridge, '.q-field :focus-visible')).toEqual([]);
    expect(bodiesOf(bridge, '.q-field__native:focus-visible').join(';')).toContain('outline: none');
  });

  it('q-badge 的文字色隨底色換成對應的 on-* token', () => {
    expect(bodiesOf(bridge, '.q-badge').join(';')).toContain('var(--t-on-primary)');
    expect(bodiesOf(bridge, '.q-badge.bg-primary').join(';')).toContain('var(--t-on-primary)');
    expect(bodiesOf(bridge, '.q-badge.bg-negative').join(';')).toContain('var(--t-on-negative)');
  });

  it('Quasar 內部的灰字 class 改用 muted', () => {
    expect(bodiesOf(bridge, '.text-grey-8').join(';')).toMatch(/color:\s*var\(--t-muted\)\s*!important/);
  });

  it('頂列上的麵包屑不用主色，改繼承頂列前景色', () => {
    expect(bodiesOf(bridge, '.theme-breadcrumb .text-primary').join(';')).toMatch(/color:\s*inherit\s*!important/);
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

describe('新四種風格的橋接', () => {
  const config = readFileSync(resolve(process.cwd(), 'quasar.config.ts'), 'utf8');
  const raw = readFileSync(resolve(process.cwd(), 'src/css/themes/quasar-bridge.css'), 'utf8');

  it('quasar.config 載入每個風格檔，且排在 bridge 之前', () => {
    for (const s of ['workbench', 'airy', 'enterprise', 'industrial', 'glass', 'bento', 'command', 'botanical']) {
      expect(config, s).toContain(`themes/${s}.css`);
      expect(config.indexOf(`themes/${s}.css`), s).toBeLessThan(config.indexOf('themes/quasar-bridge.css'));
    }
  });

  it('玻璃效果由 token 驅動，並有 prefers-reduced-transparency 與不支援 backdrop-filter 的實心退路', () => {
    expect(bridge).toMatch(/backdrop-filter:\s*blur\(var\(--t-chrome-blur\)\)/);
    expect(bridge).toMatch(/color-mix\(in srgb, var\(--t-sidebar-bg\) var\(--t-chrome-opacity\)/);
    expect(raw).toMatch(/@media \(prefers-reduced-transparency: reduce\)[^@]*--t-chrome-opacity:\s*100%/);
    expect(raw).toMatch(/@supports not \(\(backdrop-filter: blur\(1px\)\) or \(-webkit-backdrop-filter: blur\(1px\)\)\)[^@]*--t-chrome-opacity:\s*100%/);
  });

  it('背景圖與標題字型走 token', () => {
    expect(bodiesOf(bridge, '[data-style] body').join(';')).toContain('var(--t-bg-image)');
    expect(bridge).toMatch(/\.text-h6[^{]*\{[^}]*font-family:\s*var\(--t-heading-font\)/);
  });

  it('指令台：狀態徽章改成小圓點，不再是色塊', () => {
    expect(bodiesOf(bridge, "[data-style='command'] .q-badge::before").join(';')).toMatch(/border-radius:\s*50%/);
    expect(bodiesOf(bridge, "[data-style='command'] .q-badge").join(';')).toMatch(/background:\s*transparent\s*!important/);
  });

  it('便當格：統計卡片與首頁方塊有大小不一的格線，第一塊最大', () => {
    expect(bodiesOf(bridge, "[data-style='bento'] .tile-grid").join(';')).toContain('display: grid');
    expect(bodiesOf(bridge, "[data-style='bento'] .tile-grid > :first-child:nth-last-child(n + 4)").join(';')).toMatch(/grid-column:\s*span 2/);
    expect(bodiesOf(bridge, "[data-style='bento'] .stat-tiles > :first-child .text-stat").join(';')).toMatch(/font-size:\s*calc\(var\(--t-stat-size\)/);
  });
});

describe('print.css', () => {
  it('列印不受風格影響：背景圖、半透明與模糊都關掉', () => {
    const block = bodiesOf(print, '[data-style][data-mode]').join(';');
    expect(block).toMatch(/--t-bg-image:\s*none/);
    expect(block).toMatch(/--t-chrome-opacity:\s*100%/);
    expect(block).toMatch(/--t-panel-opacity:\s*100%/);
    expect(block).toMatch(/--t-chrome-blur:\s*0px/);
  });

  it('在 @media print 內強制白底黑字', () => {
    expect(print).toContain('@media print');
    const body = bodiesOf(print, 'body').join(';');
    expect(body).toMatch(/background:\s*#fff/);
    expect(body).toMatch(/color:\s*#000/);
    for (const t of ['--t-bg', '--t-panel', '--t-text', '--t-topbar-bg', '--t-sidebar-bg']) expect(print).toContain(t);
  });

  it('Quasar 的間距工具類改接 --t-space-* token，不是固定像素', () => {
    for (const size of ['xs', 'sm', 'md', 'lg', 'xl']) {
      const v = `var\\(--t-space-${size}\\)`;
      expect(bodiesOf(bridge, `.q-pa-${size}`).join(';')).toMatch(new RegExp(`padding:\\s*${v}`));
      expect(bodiesOf(bridge, `.q-mb-${size}`).join(';')).toMatch(new RegExp(`margin-bottom:\\s*${v}`));
      expect(bodiesOf(bridge, `.q-gutter-${size} > *`).join(';')).toMatch(new RegExp(`margin-top:\\s*${v}`));
      expect(bodiesOf(bridge, `.q-col-gutter-${size} > *`).join(';')).toMatch(new RegExp(`padding-left:\\s*${v}`));
    }
  });

  it('窄螢幕的對話框不得比視窗寬：min-width 被蓋掉，手機的按鈕列固定在底部', () => {
    expect(bridge).toMatch(/@media \(max-width: 700px\)\s*\{[^@]*\.q-dialog__inner--minimized > \.q-card\s*\{[^}]*min-width:\s*0\s*!important/);
    expect(bridge).toMatch(/@media \(max-width: 599\.98px\)[\s\S]*\.q-card__actions\s*\{[^}]*position:\s*sticky/);
  });

  it('觸控裝置的可點擊目標至少 44px', () => {
    const coarse = bridge.slice(bridge.indexOf('@media (pointer: coarse)'));
    expect(coarse).toMatch(/\.q-btn--rectangle[^{]*\{[^}]*min-height:\s*44px/);
    expect(coarse).toMatch(/\.q-field--dense \.q-field__control[^{]*\{[^}]*max\(var\(--t-field-height\), 44px\)/);
  });
});
