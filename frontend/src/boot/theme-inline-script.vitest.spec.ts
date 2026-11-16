import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { THEME_STYLES } from 'src/domain/theme/theme-model';
import { THEME_CACHE_KEY, THEME_DEMO_KEY } from 'src/services/theme/theme-storage';

const html = readFileSync(resolve(process.cwd(), 'index.html'), 'utf8');
const script = /<script data-theme-bootstrap>([\s\S]*?)<\/script>/.exec(html)?.[1] ?? '';
const root = document.documentElement;

function run() {
  new Function(script)();
}

function stubPrefersDark(dark: boolean) {
  vi.stubGlobal('matchMedia', () => ({ matches: dark }));
  window.matchMedia = (() => ({ matches: dark })) as unknown as typeof window.matchMedia;
}

describe('index.html 內嵌主題 script', () => {
  beforeEach(() => {
    localStorage.clear();
    root.removeAttribute('data-style');
    root.removeAttribute('data-mode');
  });
  afterEach(() => vi.unstubAllGlobals());

  it('head 裡有 script', () => {
    expect(script.length).toBeGreaterThan(0);
    expect(html.indexOf('data-theme-bootstrap')).toBeLessThan(html.indexOf('</head>'));
  });

  it.each([
    ['快取 dark + airy', '{"mode":"dark","style":"airy"}', false, 'dark', 'airy'],
    ['快取 system，瀏覽器偏好深色', '{"mode":"system","style":"enterprise"}', true, 'dark', 'enterprise'],
    ['快取 system，瀏覽器偏好淺色', '{"mode":"system","style":"enterprise"}', false, 'light', 'enterprise'],
    ['沒有快取', null, false, 'light', 'workbench'],
    ['沒有快取，瀏覽器偏好深色', null, true, 'dark', 'workbench'],
    ['JSON 損毀', '{oops', true, 'dark', 'workbench'],
    ['快取是 null', 'null', false, 'light', 'workbench'],
    ['快取 glass', '{"mode":"light","style":"glass"}', false, 'light', 'glass'],
    ['快取 bento', '{"mode":"dark","style":"bento"}', false, 'dark', 'bento'],
    ['快取 command', '{"mode":"light","style":"command"}', false, 'light', 'command'],
    ['快取 botanical', '{"mode":"dark","style":"botanical"}', false, 'dark', 'botanical'],
    ['style 未知', '{"mode":"light","style":"neon"}', false, 'light', 'workbench'],
    ['mode 大小寫不符', '{"mode":"Dark","style":"airy"}', false, 'light', 'airy'],
  ])('%s', (_name, cache, prefersDark, mode, style) => {
    stubPrefersDark(prefersDark);
    if (cache !== null) localStorage.setItem(THEME_CACHE_KEY, cache);
    run();
    expect(root.getAttribute('data-mode')).toBe(mode);
    expect(root.getAttribute('data-style')).toBe(style);
  });

  it.each(THEME_STYLES)('內嵌 script 認得 THEME_STYLES 裡的 %s', (style) => {
    stubPrefersDark(false);
    localStorage.setItem(THEME_CACHE_KEY, JSON.stringify({ mode: 'light', style }));
    run();
    expect(root.getAttribute('data-style')).toBe(style);
  });

  it('localStorage 存取丟例外時仍設定屬性', () => {
    stubPrefersDark(true);
    vi.stubGlobal('localStorage', { getItem: () => { throw new Error('denied'); } });
    run();
    expect(root.getAttribute('data-mode')).toBe('dark');
    expect(root.getAttribute('data-style')).toBe('workbench');
  });

  it('matchMedia 不存在時 data-mode 為 light', () => {
    vi.stubGlobal('matchMedia', undefined);
    window.matchMedia = undefined as unknown as typeof window.matchMedia;
    localStorage.setItem(THEME_CACHE_KEY, '{"mode":"system","style":"airy"}');
    run();
    expect(root.getAttribute('data-mode')).toBe('light');
    expect(root.getAttribute('data-style')).toBe('airy');
  });

  it('matchMedia 丟例外時 data-mode 為 light', () => {
    vi.stubGlobal('matchMedia', () => { throw new Error('boom'); });
    window.matchMedia = (() => { throw new Error('boom'); }) as unknown as typeof window.matchMedia;
    run();
    expect(root.getAttribute('data-mode')).toBe('light');
  });

  it('只讀快取鍵，不讀 Demo 鍵', () => {
    stubPrefersDark(false);
    localStorage.setItem(THEME_DEMO_KEY, '{"mode":"dark","style":"industrial"}');
    run();
    expect(root.getAttribute('data-style')).toBe('workbench');
    expect(root.getAttribute('data-mode')).toBe('light');
    expect(script).not.toContain(THEME_DEMO_KEY);
  });

  it('script 使用的鍵與 THEME_CACHE_KEY 相同', () => {
    expect(script).toContain(`'${THEME_CACHE_KEY}'`);
  });
});
