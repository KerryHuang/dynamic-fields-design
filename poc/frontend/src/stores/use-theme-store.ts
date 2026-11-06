import { defineStore } from 'pinia';
import { Dark } from 'quasar';
import { computed, ref } from 'vue';
import { FACTORY_THEME, SIDEBAR_WIDTH, resolveTheme } from 'src/domain/theme/theme-model';
import type { MeTheme, ResolvedMode, ThemeChoice, ThemeDefaults } from 'src/domain/theme/theme-model';
import { readDemoChoice, readThemeCache, writeDemoChoice, writeThemeCache } from 'src/services/theme/theme-storage';

function systemPrefersDark(): boolean {
  try {
    return typeof window.matchMedia === 'function' && window.matchMedia('(prefers-color-scheme: dark)').matches;
  } catch {
    return false;
  }
}

/** 主題狀態：把解析結果寫到 <html data-style／data-mode> 並同步 Quasar Dark。 */
export const useThemeStore = defineStore('theme', () => {
  const choice = ref<ThemeChoice>({ mode: null, style: null });
  const defaults = ref<ThemeDefaults>({ ...FACTORY_THEME });
  const canSave = ref(true);
  const systemDark = ref(systemPrefersDark());
  /** 列印期間暫時強制淺色。 */
  const printing = ref(false);

  const effective = computed(() => resolveTheme(choice.value, defaults.value));
  const resolvedMode = computed<ResolvedMode>(() => {
    if (printing.value) return 'light';
    const m = effective.value.mode;
    return m === 'system' ? (systemDark.value ? 'dark' : 'light') : m;
  });
  const appliedKey = computed(() => `${effective.value.style}:${resolvedMode.value}`);
  const sidebarWidth = computed(() => SIDEBAR_WIDTH[effective.value.style]);

  /** 同步寫入 DOM，讀 appliedKey 的圖表才能立刻讀到新 token。 */
  function apply(persist: boolean) {
    const root = document.documentElement;
    root.setAttribute('data-style', effective.value.style);
    root.setAttribute('data-mode', resolvedMode.value);
    Dark.set(resolvedMode.value === 'dark');
    if (persist) writeThemeCache(effective.value);
  }

  let hydrated = false;
  function hydrate() {
    const cached = readThemeCache();
    if (cached) defaults.value = cached;
    systemDark.value = systemPrefersDark();
    apply(false);
    if (hydrated) return;
    hydrated = true;
    try {
      window.matchMedia?.('(prefers-color-scheme: dark)').addEventListener('change', (e) => {
        systemDark.value = e?.matches ?? systemPrefersDark();
        apply(false);
      });
    } catch {
      /* 舊瀏覽器沒有 addEventListener：不跟隨變化 */
    }
    window.addEventListener('beforeprint', () => { printing.value = true; apply(false); });
    window.addEventListener('afterprint', () => { printing.value = false; apply(false); });
  }

  function applyMe(t: MeTheme) {
    canSave.value = t.canSave;
    defaults.value = { mode: t.defaultMode, style: t.defaultStyle };
    choice.value = t.canSave ? { mode: t.mode, style: t.style } : readDemoChoice();
    apply(true);
  }

  function applyAnonymous(d: ThemeDefaults) {
    defaults.value = d;
    choice.value = readDemoChoice();
    apply(true);
  }

  function setChoice(c: ThemeChoice) {
    choice.value = { ...c };
    if (!canSave.value) writeDemoChoice(c);
    apply(true);
  }

  return { choice, defaults, canSave, effective, resolvedMode, appliedKey, sidebarWidth, hydrate, applyMe, applyAnonymous, setChoice };
});
