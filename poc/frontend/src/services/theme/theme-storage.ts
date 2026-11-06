import { FACTORY_THEME, parseMode, parseStyle } from 'src/domain/theme/theme-model';
import type { ThemeChoice, ThemeDefaults } from 'src/domain/theme/theme-model';

/** 只放視覺偏好；所有函式內部 try/catch（無痕模式、被封鎖時 localStorage 會丟例外）。 */
export const THEME_CACHE_KEY = 'formwright.theme.cache';
export const THEME_DEMO_KEY = 'formwright.theme.demo';

function readJson(key: string): Record<string, unknown> | null {
  try {
    const raw = localStorage.getItem(key);
    if (raw === null) return null;
    const v: unknown = JSON.parse(raw);
    return v !== null && typeof v === 'object' ? (v as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

function write(key: string, value: unknown) {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch {
    /* 不可用時照常運作，只是不記住 */
  }
}

/** 逐欄 parse，未知值用出廠值；無快取或 JSON 損毀回 null。 */
export function readThemeCache(): ThemeDefaults | null {
  const v = readJson(THEME_CACHE_KEY);
  if (!v) return null;
  return { mode: parseMode(v.mode) ?? FACTORY_THEME.mode, style: parseStyle(v.style) ?? FACTORY_THEME.style };
}

/** mode 存未解析的值（可能是 system）。 */
export function writeThemeCache(t: ThemeDefaults): void {
  write(THEME_CACHE_KEY, { mode: t.mode, style: t.style });
}

export function readDemoChoice(): ThemeChoice {
  const v = readJson(THEME_DEMO_KEY);
  return { mode: parseMode(v?.mode), style: parseStyle(v?.style) };
}

export function writeDemoChoice(c: ThemeChoice): void {
  write(THEME_DEMO_KEY, { mode: c.mode, style: c.style });
}
