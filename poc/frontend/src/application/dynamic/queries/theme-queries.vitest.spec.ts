import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import { Dark } from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createApp, reactive } from 'vue';
import type { Me } from 'src/domain/dynamic/types';
import type { MeTheme, ThemeChoice } from 'src/domain/theme/theme-model';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { THEME_CACHE_KEY, THEME_DEMO_KEY } from 'src/services/theme/theme-storage';
import { useIdentityStore } from 'src/stores/use-identity-store';
import { useThemeStore } from 'src/stores/use-theme-store';
import { useSaveSiteTheme, useSelectTheme, useThemeSync } from './theme-queries';

const route = reactive<{ meta: { public?: boolean } }>({ meta: {} });
vi.mock('vue-router', () => ({ useRoute: () => route }));

const html = document.documentElement;
const mode = () => html.getAttribute('data-mode');
const style = () => html.getAttribute('data-style');

const theme = (over: Partial<MeTheme> = {}): MeTheme => ({
  mode: null, style: null, effectiveMode: 'light', effectiveStyle: 'workbench',
  defaultMode: 'light', defaultStyle: 'workbench', canSave: true, ...over,
});
const me = (t: MeTheme = theme()): Me => ({
  userId: 'u1', displayName: 'U', mustChangePassword: false, isSystemAdmin: false, groups: [],
  designer: { read: false, add: false, edit: false, delete: false }, theme: t,
});

function deferred<T>() {
  let resolve!: (v: T) => void;
  let reject!: (e: unknown) => void;
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}

let qc: QueryClient;
/** 每個測試結束都卸載，否則上一個測試的 watcher 會跟著共用的 route 改到這個測試的 DOM。 */
const mounted: ReturnType<typeof createApp>[] = [];
function mount<T>(setup: () => T): T {
  let result!: T;
  const app = createApp({ setup: () => { result = setup(); return () => null; } }).use(VueQueryPlugin, { queryClient: qc });
  app.mount(document.createElement('div'));
  mounted.push(app);
  return result;
}

describe('theme-queries', () => {
  beforeEach(() => {
    localStorage.clear();
    html.removeAttribute('data-style');
    html.removeAttribute('data-mode');
    Dark.set(false);
    route.meta = {};
    qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
    setActivePinia(createPinia());
    vi.restoreAllMocks();
  });
  afterEach(() => {
    mounted.splice(0).forEach((a) => a.unmount());
    vi.restoreAllMocks();
  });

  describe('useThemeSync', () => {
    it('已登入時用 /me 套用，不打 GET /theme', async () => {
      useIdentityStore().setSession('u1');
      const getMe = vi.spyOn(dynamicApi, 'getMe').mockResolvedValue(me(theme({ mode: 'dark', style: 'airy', effectiveMode: 'dark', effectiveStyle: 'airy' })));
      const defaults = vi.spyOn(dynamicApi, 'getThemeDefaults');
      mount(useThemeSync);
      await vi.waitFor(() => expect(style()).toBe('airy'));
      expect(mode()).toBe('dark');
      expect(getMe).toHaveBeenCalledTimes(1);
      expect(defaults).not.toHaveBeenCalled();
    });

    it('guard 還沒跑完（起始路由、未登入）時不打 GET /theme 也不打 /me', async () => {
      const getMe = vi.spyOn(dynamicApi, 'getMe');
      const defaults = vi.spyOn(dynamicApi, 'getThemeDefaults');
      route.meta = {};
      mount(useThemeSync);
      await flushPromises();
      expect(defaults).not.toHaveBeenCalled();
      expect(getMe).not.toHaveBeenCalled();
    });

    it('未登入且在 public 路由時用 GET /theme 校正', async () => {
      route.meta = { public: true };
      const getMe = vi.spyOn(dynamicApi, 'getMe');
      vi.spyOn(dynamicApi, 'getThemeDefaults').mockResolvedValue({ mode: 'dark', style: 'enterprise' });
      mount(useThemeSync);
      await vi.waitFor(() => expect(style()).toBe('enterprise'));
      expect(mode()).toBe('dark');
      expect(getMe).not.toHaveBeenCalled();
    });

    it('GET /theme 失敗時保留快取的主題，不丟例外', async () => {
      localStorage.setItem(THEME_CACHE_KEY, JSON.stringify({ mode: 'dark', style: 'airy' }));
      useThemeStore().hydrate();
      route.meta = { public: true };
      const defaults = vi.spyOn(dynamicApi, 'getThemeDefaults').mockRejectedValue(new Error('down'));
      mount(useThemeSync);
      await vi.waitFor(() => expect(defaults).toHaveBeenCalled());
      await flushPromises();
      expect(style()).toBe('airy');
      expect(mode()).toBe('dark');
    });

    it('登出後回到登入頁會重新查全站預設', async () => {
      const identity = useIdentityStore();
      route.meta = { public: true };
      const defaults = vi.spyOn(dynamicApi, 'getThemeDefaults').mockResolvedValue({ mode: 'light', style: 'airy' });
      vi.spyOn(dynamicApi, 'getMe').mockResolvedValue(me());
      mount(useThemeSync);
      await vi.waitFor(() => expect(defaults).toHaveBeenCalledTimes(1));

      identity.setSession('u1');
      route.meta = {};
      await flushPromises();
      identity.clear();
      qc.clear(); // handleUnauthorized 與登出都會清掉整個快取
      route.meta = { public: true };
      await vi.waitFor(() => expect(defaults).toHaveBeenCalledTimes(2));
    });
  });

  describe('useSelectTheme', () => {
    function setup(t: MeTheme = theme()) {
      useIdentityStore().setSession('u1');
      qc.setQueryData(['me', 'u1'], me(t));
      vi.spyOn(dynamicApi, 'getMe').mockResolvedValue(me(t));
      const themeStore = useThemeStore();
      themeStore.applyMe(t);
      return mount(() => { useThemeSync(); return useSelectTheme(); });
    }
    const pick = (mode: ThemeChoice['mode']): ThemeChoice => ({ mode, style: null });

    it('Demo 站選擇不打 API，寫進瀏覽器', async () => {
      const save = vi.spyOn(dynamicApi, 'saveMyTheme');
      const select = setup(theme({ canSave: false }));
      const onError = vi.fn();
      select({ mode: 'dark', style: 'airy' }, onError);
      await flushPromises();
      expect(save).not.toHaveBeenCalled();
      expect(mode()).toBe('dark');
      expect(style()).toBe('airy');
      expect(JSON.parse(localStorage.getItem(THEME_DEMO_KEY)!)).toEqual({ mode: 'dark', style: 'airy' });
      expect(onError).not.toHaveBeenCalled();
    });

    it('選了立刻生效，成功後以伺服器回應為準', async () => {
      const d = deferred<MeTheme>();
      vi.spyOn(dynamicApi, 'saveMyTheme').mockReturnValue(d.promise);
      const select = setup();
      select(pick('dark'), vi.fn());
      await vi.waitFor(() => expect(mode()).toBe('dark'));
      const server = theme({ mode: 'dark', effectiveMode: 'dark' });
      d.resolve(server);
      await vi.waitFor(() => expect((qc.getQueryData(['me', 'u1']) as Me).theme).toEqual(server));
    });

    it('儲存失敗會回到上一個設定並呼叫 onError', async () => {
      vi.spyOn(dynamicApi, 'saveMyTheme').mockRejectedValue(new Error('500'));
      const select = setup(theme({ mode: 'light', effectiveMode: 'light' }));
      const onError = vi.fn();
      select(pick('dark'), onError);
      await vi.waitFor(() => expect(onError).toHaveBeenCalledTimes(1));
      await vi.waitFor(() => expect(mode()).toBe('light'));
      expect(useThemeStore().choice.mode).toBe('light');
    });

    it('前一次失敗、後一次還在送時，畫面留在後一次的選擇', async () => {
      const second = deferred<MeTheme>();
      vi.spyOn(dynamicApi, 'saveMyTheme')
        .mockRejectedValueOnce(new Error('500'))
        .mockReturnValueOnce(second.promise);
      const select = setup(theme({ mode: 'light', effectiveMode: 'light' }));
      const onError = vi.fn();
      select(pick('dark'), onError);
      select({ mode: 'light', style: 'airy' }, onError);
      await vi.waitFor(() => expect(onError).toHaveBeenCalledTimes(1));
      await flushPromises();
      expect(mode()).toBe('light');
      expect(style()).toBe('airy');
      second.resolve(theme({ mode: 'light', style: 'airy', effectiveMode: 'light', effectiveStyle: 'airy' }));
      await vi.waitFor(() => expect((qc.getQueryData(['me', 'u1']) as Me).theme.style).toBe('airy'));
      expect(style()).toBe('airy');
    });

    it('連續兩次都失敗，回到第一次之前的設定', async () => {
      vi.spyOn(dynamicApi, 'saveMyTheme').mockRejectedValue(new Error('500'));
      const select = setup(theme({ mode: 'light', style: 'industrial', effectiveMode: 'light', effectiveStyle: 'industrial' }));
      const onError = vi.fn();
      select(pick('dark'), onError);
      select({ mode: 'dark', style: 'airy' }, onError);
      await vi.waitFor(() => expect(onError).toHaveBeenCalledTimes(2));
      await vi.waitFor(() => expect(style()).toBe('industrial'));
      expect(mode()).toBe('light');
    });

    it('儲存中收到舊的 /me 不會蓋掉剛選的值', async () => {
      const d = deferred<MeTheme>();
      vi.spyOn(dynamicApi, 'saveMyTheme').mockReturnValue(d.promise);
      const select = setup();
      select(pick('dark'), vi.fn());
      await vi.waitFor(() => expect(mode()).toBe('dark'));
      qc.setQueryData(['me', 'u1'], me(theme({ mode: 'light', effectiveMode: 'light' }))); // 在途的舊資料
      await flushPromises();
      expect(mode()).toBe('dark');
      d.resolve(theme({ mode: 'dark', effectiveMode: 'dark' }));
      await vi.waitFor(() => expect((qc.getQueryData(['me', 'u1']) as Me).theme.mode).toBe('dark'));
      expect(mode()).toBe('dark');
    });
  });

  describe('useSaveSiteTheme', () => {
    it('管理員存全站預設後 /me 重新取得', async () => {
      vi.spyOn(dynamicApi, 'saveSiteTheme').mockResolvedValue({ mode: 'dark', style: 'airy' });
      const invalidate = vi.spyOn(qc, 'invalidateQueries');
      const m = mount(useSaveSiteTheme);
      await m.mutateAsync({ mode: 'dark', style: 'airy' });
      expect(invalidate.mock.calls.map((c) => (c[0] as { queryKey?: unknown[] }).queryKey)).toContainEqual(['me']);
    });
  });
});
