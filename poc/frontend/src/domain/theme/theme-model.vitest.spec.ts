import { describe, expect, it } from 'vitest';
import { FACTORY_THEME, SIDEBAR_WIDTH, parseMode, parseStyle, resolveTheme } from './theme-model';

describe('theme-model', () => {
  it('resolveTheme 逐欄：個人 > 預設', () => {
    expect(resolveTheme({ mode: 'dark', style: null }, { mode: 'light', style: 'airy' })).toEqual({ mode: 'dark', style: 'airy' });
    expect(resolveTheme({ mode: null, style: 'industrial' }, { mode: 'light', style: 'airy' })).toEqual({ mode: 'light', style: 'industrial' });
  });

  it('parseMode 區分大小寫且拒絕未知值', () => {
    expect(parseMode('dark')).toBe('dark');
    expect(parseMode('Dark')).toBeNull();
    expect(parseMode('neon')).toBeNull();
    expect(parseMode(1)).toBeNull();
  });

  it('parseStyle 區分大小寫且拒絕未知值', () => {
    expect(parseStyle('airy')).toBe('airy');
    expect(parseStyle('Airy')).toBeNull();
    expect(parseStyle(null)).toBeNull();
  });

  it('出廠值與邊欄寬', () => {
    expect(FACTORY_THEME).toEqual({ mode: 'system', style: 'workbench' });
    expect(SIDEBAR_WIDTH).toEqual({ workbench: 172, airy: 188, enterprise: 208, industrial: 186 });
  });
});
