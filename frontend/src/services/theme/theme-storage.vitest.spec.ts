import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { THEME_CACHE_KEY, THEME_DEMO_KEY, readDemoChoice, readThemeCache, writeDemoChoice, writeThemeCache } from './theme-storage';

describe('theme-storage', () => {
  beforeEach(() => localStorage.clear());
  afterEach(() => vi.unstubAllGlobals());

  it('快取往返，mode 保留未解析的 system', () => {
    writeThemeCache({ mode: 'system', style: 'airy' });
    expect(readThemeCache()).toEqual({ mode: 'system', style: 'airy' });
    expect(localStorage.getItem(THEME_CACHE_KEY)).not.toBeNull();
  });

  it('沒有快取回 null', () => {
    expect(readThemeCache()).toBeNull();
  });

  it('快取損毀 JSON 回 null', () => {
    localStorage.setItem(THEME_CACHE_KEY, '{oops');
    expect(readThemeCache()).toBeNull();
  });

  it('快取單一欄位未知時只有那一欄退回出廠值', () => {
    localStorage.setItem(THEME_CACHE_KEY, JSON.stringify({ mode: 'dark', style: 'neon' }));
    expect(readThemeCache()).toEqual({ mode: 'dark', style: 'workbench' });
  });

  it('快取不是物件時回 null', () => {
    localStorage.setItem(THEME_CACHE_KEY, 'null');
    expect(readThemeCache()).toBeNull();
    localStorage.setItem(THEME_CACHE_KEY, '"dark"');
    expect(readThemeCache()).toBeNull();
  });

  it('Demo 偏好往返，與快取用不同的鍵', () => {
    writeDemoChoice({ mode: 'dark', style: null });
    expect(readDemoChoice()).toEqual({ mode: 'dark', style: null });
    expect(THEME_DEMO_KEY).not.toBe(THEME_CACHE_KEY);
    expect(readThemeCache()).toBeNull();
  });

  it('Demo 偏好損毀或未知值時逐欄退回 null', () => {
    localStorage.setItem(THEME_DEMO_KEY, '{oops');
    expect(readDemoChoice()).toEqual({ mode: null, style: null });
    localStorage.setItem(THEME_DEMO_KEY, JSON.stringify({ mode: 'neon', style: 'airy' }));
    expect(readDemoChoice()).toEqual({ mode: null, style: 'airy' });
  });

  it('localStorage 丟例外時讀回 null、寫入不丟例外', () => {
    vi.stubGlobal('localStorage', {
      getItem: () => { throw new Error('denied'); },
      setItem: () => { throw new Error('denied'); },
    });
    expect(readThemeCache()).toBeNull();
    expect(readDemoChoice()).toEqual({ mode: null, style: null });
    expect(() => writeThemeCache({ mode: 'dark', style: 'airy' })).not.toThrow();
    expect(() => writeDemoChoice({ mode: 'dark', style: 'airy' })).not.toThrow();
  });
});
