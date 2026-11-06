export const THEME_MODES = ['light', 'dark', 'system'] as const;
export const THEME_STYLES = ['workbench', 'airy', 'enterprise', 'industrial'] as const;
export type ThemeMode = (typeof THEME_MODES)[number];
export type ThemeStyle = (typeof THEME_STYLES)[number];
export type ResolvedMode = 'light' | 'dark';

/** 個人設定；null 代表跟隨全站預設。 */
export interface ThemeChoice { mode: ThemeMode | null; style: ThemeStyle | null }
export interface ThemeDefaults { mode: ThemeMode; style: ThemeStyle }
export interface MeTheme extends ThemeChoice {
  effectiveMode: ThemeMode; effectiveStyle: ThemeStyle;
  defaultMode: ThemeMode; defaultStyle: ThemeStyle; canSave: boolean;
}

export const FACTORY_THEME: ThemeDefaults = { mode: 'system', style: 'workbench' };

/** q-drawer 的 width 只收數字，所以放這裡。 */
export const SIDEBAR_WIDTH: Record<ThemeStyle, number> = { workbench: 172, airy: 188, enterprise: 208, industrial: 186 };

export function parseMode(v: unknown): ThemeMode | null {
  return THEME_MODES.find((m) => m === v) ?? null;
}

export function parseStyle(v: unknown): ThemeStyle | null {
  return THEME_STYLES.find((s) => s === v) ?? null;
}

/** 逐欄解析：個人 > 預設。 */
export function resolveTheme(choice: ThemeChoice, defaults: ThemeDefaults): ThemeDefaults {
  return { mode: choice.mode ?? defaults.mode, style: choice.style ?? defaults.style };
}
