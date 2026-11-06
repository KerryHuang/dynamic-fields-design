import { createPinia, setActivePinia } from 'pinia';
import { Dark } from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { MeTheme } from 'src/domain/theme/theme-model';
import { THEME_CACHE_KEY, THEME_DEMO_KEY } from 'src/services/theme/theme-storage';
import { useThemeStore } from './use-theme-store';

/** 可手動觸發 change 的 matchMedia。 */
function stubMatchMedia(initialDark: boolean) {
  const listeners = new Set<() => void>();
  const mql = {
    matches: initialDark,
    addEventListener: (_: string, fn: () => void) => listeners.add(fn),
    removeEventListener: (_: string, fn: () => void) => listeners.delete(fn),
  };
  vi.stubGlobal('matchMedia', () => mql);
  window.matchMedia = (() => mql) as unknown as typeof window.matchMedia;
  return { setDark(v: boolean) { mql.matches = v; listeners.forEach((f) => f()); } };
}

const me = (over: Partial<MeTheme> = {}): MeTheme => ({
  mode: null, style: null, effectiveMode: 'system', effectiveStyle: 'workbench',
  defaultMode: 'system', defaultStyle: 'workbench', canSave: true, ...over,
});
const html = () => document.documentElement;
const cache = () => JSON.parse(localStorage.getItem(THEME_CACHE_KEY) ?? 'null');

describe('theme store', () => {
  beforeEach(() => {
    localStorage.clear();
    html().removeAttribute('data-style');
    html().removeAttribute('data-mode');
    Dark.set(false);
    setActivePinia(createPinia());
  });
  afterEach(() => vi.unstubAllGlobals());

  it('hydrate 沒有快取時套用出廠值並依系統解析', () => {
    stubMatchMedia(true);
    const s = useThemeStore();
    s.hydrate();
    expect(html().getAttribute('data-style')).toBe('workbench');
    expect(html().getAttribute('data-mode')).toBe('dark');
    expect(Dark.isActive).toBe(true);
    expect(s.appliedKey).toBe('workbench:dark');
  });

  it('hydrate 讀快取', () => {
    stubMatchMedia(false);
    localStorage.setItem(THEME_CACHE_KEY, JSON.stringify({ mode: 'dark', style: 'industrial' }));
    useThemeStore().hydrate();
    expect(html().getAttribute('data-style')).toBe('industrial');
    expect(html().getAttribute('data-mode')).toBe('dark');
  });

  it('system 跟隨瀏覽器變化，不必重新整理', () => {
    const mm = stubMatchMedia(false);
    useThemeStore().hydrate();
    expect(html().getAttribute('data-mode')).toBe('light');
    mm.setDark(true);
    expect(html().getAttribute('data-mode')).toBe('dark');
    expect(Dark.isActive).toBe(true);
  });

  it('明確選 light 時不理會瀏覽器變化', () => {
    const mm = stubMatchMedia(false);
    const s = useThemeStore();
    s.hydrate();
    s.applyMe(me({ mode: 'light', effectiveMode: 'light' }));
    mm.setDark(true);
    expect(html().getAttribute('data-mode')).toBe('light');
  });

  it('applyMe 逐欄解析並把未解析的 mode 寫進快取', () => {
    stubMatchMedia(true);
    const s = useThemeStore();
    s.applyMe(me({ mode: null, style: 'airy', effectiveMode: 'system', effectiveStyle: 'airy' }));
    expect(html().getAttribute('data-style')).toBe('airy');
    expect(html().getAttribute('data-mode')).toBe('dark');
    expect(cache()).toEqual({ mode: 'system', style: 'airy' });
    expect(s.sidebarWidth).toBe(188);
  });

  it('canSave 為 false 時用瀏覽器偏好，setChoice 寫 demo 鍵且不影響快取鍵的格式', () => {
    stubMatchMedia(false);
    localStorage.setItem(THEME_DEMO_KEY, JSON.stringify({ mode: 'dark', style: null }));
    const s = useThemeStore();
    s.applyMe(me({ canSave: false, defaultStyle: 'enterprise', effectiveStyle: 'enterprise' }));
    expect(html().getAttribute('data-mode')).toBe('dark');
    expect(html().getAttribute('data-style')).toBe('enterprise');

    s.setChoice({ mode: 'light', style: 'airy' });
    expect(JSON.parse(localStorage.getItem(THEME_DEMO_KEY)!)).toEqual({ mode: 'light', style: 'airy' });
    expect(cache()).toEqual({ mode: 'light', style: 'airy' });
  });

  it('canSave 為 true 時 setChoice 不寫 demo 鍵', () => {
    stubMatchMedia(false);
    const s = useThemeStore();
    s.applyMe(me());
    s.setChoice({ mode: 'dark', style: null });
    expect(localStorage.getItem(THEME_DEMO_KEY)).toBeNull();
    expect(html().getAttribute('data-mode')).toBe('dark');
    expect(cache()).toEqual({ mode: 'dark', style: 'workbench' });
  });

  it('applyAnonymous 沒有 Demo 偏好時用全站預設', () => {
    stubMatchMedia(false);
    const s = useThemeStore();
    s.applyAnonymous({ mode: 'dark', style: 'enterprise' });
    expect(html().getAttribute('data-style')).toBe('enterprise');
    expect(html().getAttribute('data-mode')).toBe('dark');
  });

  it('applyAnonymous 有 Demo 偏好時優先', () => {
    stubMatchMedia(false);
    localStorage.setItem(THEME_DEMO_KEY, JSON.stringify({ mode: 'light', style: 'industrial' }));
    useThemeStore().applyAnonymous({ mode: 'dark', style: 'enterprise' });
    expect(html().getAttribute('data-style')).toBe('industrial');
    expect(html().getAttribute('data-mode')).toBe('light');
  });

  it('localStorage 不可用時 setChoice 仍立刻生效', () => {
    stubMatchMedia(false);
    vi.stubGlobal('localStorage', {
      getItem: () => { throw new Error('denied'); },
      setItem: () => { throw new Error('denied'); },
    });
    const s = useThemeStore();
    s.hydrate();
    expect(() => s.setChoice({ mode: 'dark', style: 'airy' })).not.toThrow();
    expect(html().getAttribute('data-mode')).toBe('dark');
    expect(html().getAttribute('data-style')).toBe('airy');
  });

  it('beforeprint 暫時切成淺色、afterprint 還原，且不寫快取', () => {
    stubMatchMedia(false);
    const s = useThemeStore();
    s.hydrate();
    s.applyMe(me({ mode: 'dark', effectiveMode: 'dark' }));
    const before = localStorage.getItem(THEME_CACHE_KEY);
    window.dispatchEvent(new Event('beforeprint'));
    expect(html().getAttribute('data-mode')).toBe('light');
    expect(Dark.isActive).toBe(false);
    expect(s.appliedKey).toBe('workbench:light');
    expect(localStorage.getItem(THEME_CACHE_KEY)).toBe(before);
    window.dispatchEvent(new Event('afterprint'));
    expect(html().getAttribute('data-mode')).toBe('dark');
    expect(Dark.isActive).toBe(true);
    expect(s.appliedKey).toBe('workbench:dark');
  });

  it('matchMedia 不存在時視為淺色，不丟例外', () => {
    vi.stubGlobal('matchMedia', undefined);
    window.matchMedia = undefined as unknown as typeof window.matchMedia;
    const s = useThemeStore();
    expect(() => s.hydrate()).not.toThrow();
    expect(html().getAttribute('data-mode')).toBe('light');
  });

  it('hydrate 重複呼叫不會重複註冊監聽', () => {
    const mm = stubMatchMedia(false);
    const s = useThemeStore();
    s.hydrate();
    s.hydrate();
    mm.setDark(true);
    expect(html().getAttribute('data-mode')).toBe('dark');
  });
});
