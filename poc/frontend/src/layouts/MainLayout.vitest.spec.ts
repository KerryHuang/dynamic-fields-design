import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import zhTW from 'src/i18n/zh-TW.json';
import ListPage from 'src/pages/dynamic/ListPage.vue';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { http } from 'src/services/api/http';
import { useIdentityStore } from 'src/stores/use-identity-store';
import MainLayout from './MainLayout.vue';

vi.mock('src/services/api/dynamic-api', async (importOriginal) => ({
  dynamicApi: {
    ...(await importOriginal<typeof import('src/services/api/dynamic-api')>()).dynamicApi,
    getModuleSchema: vi.fn(), getViews: vi.fn(), getPagedRecords: vi.fn(),
  },
}));
const api = vi.mocked(dynamicApi);

const USERS = ['admin', 'sales01'].map((userId) => ({ userId, displayName: userId }));
const me = (designerRead: boolean) => ({
  userId: 'admin', displayName: 'admin', isSystemAdmin: false, groups: [],
  designer: { read: designerRead, add: false, edit: false, delete: false },
});

describe('MainLayout menu', () => {
  beforeEach(() => {
    localStorage.clear();
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  it('refetches the menu after switching identity', async () => {
    const pinia = createPinia();
    setActivePinia(pinia);
    const seen: string[] = [];
    http.defaults.adapter = async (config) => {
      const user = config.headers.get('X-User') as string;
      let data: unknown = [];
      if (config.url === '/users') data = USERS;
      else if (config.url === '/me') data = me(true);
      else { seen.push(user); data = [{ key: 'm', name: { 'zh-TW': `menu-${user}` } }]; }
      return { data, status: 200, statusText: 'OK', headers: {}, config };
    };
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', component: { template: '<i/>' } }] });
    const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
    const w = mount(MainLayout, {
      global: { plugins: [[quasar.Quasar, { components: quasar }], pinia, router, i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
    });
    await flushPromises();
    expect(seen).toEqual(['admin']);
    expect(w.text()).toContain('menu-admin');

    (w.vm as unknown as { switchUser: (id: string) => void }).switchUser('sales01');
    await flushPromises();
    expect(seen).toEqual(['admin', 'sales01']);
    expect(w.text()).toContain('menu-sales01');
  });

  it('groups menu items by section, unsectioned under 自訂功能 first, sections sorted by name', async () => {
    const pinia = createPinia();
    setActivePinia(pinia);
    const item = (key: string, section?: string) => ({ key, name: { 'zh-TW': `item-${key}` }, ...(section ? { section } : {}) });
    http.defaults.adapter = async (config) => {
      const data = config.url === '/me' ? me(false) : config.url === '/users' ? USERS
        : [item('a', 'Zeta'), item('b'), item('c', 'Alpha'), item('d', 'Zeta'), item('e')];
      return { data, status: 200, statusText: 'OK', headers: {}, config };
    };
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', component: { template: '<i/>' } }] });
    const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
    const w = mount(MainLayout, {
      global: { plugins: [[quasar.Quasar, { components: quasar }], pinia, router, i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
    });
    await flushPromises();
    const text = w.find('.q-drawer').text();
    const order = ['自訂功能', 'item-b', 'item-e', 'Alpha', 'item-c', 'Zeta', 'item-a', 'item-d'].map((s) => text.indexOf(s));
    expect(order.every((i) => i >= 0)).toBe(true);
    expect(order).toEqual([...order].sort((x, y) => x - y));
    expect(w.findAll('.q-item__label--header').map((h) => h.text())).toEqual(['自訂功能', 'Alpha', 'Zeta']);
  });

  it('refetches views and re-picks the default view after switching identity', async () => {
    const pinia = createPinia();
    setActivePinia(pinia);
    http.defaults.adapter = async (config) => ({
      data: config.url === '/me' ? me(true) : [], status: 200, statusText: 'OK', headers: {}, config,
    });
    const text = (label: string) => ({ 'zh-TW': label });
    api.getModuleSchema.mockResolvedValue({
      key: 'm', programCode: 'DYN_M', name: text('模組'), titleFieldKey: 'f_1', version: 1, rules: [], permissions: { read: true, add: true, edit: true, delete: true, printExport: true, pricing: true },
      blocks: [{ id: 1, label: text('區'), collapsed: false, columns: 2, fields: ['標題', '甲'].map((l, i) => ({
        id: i + 1, key: `f_${i + 1}`, type: 'text' as const, label: text(l), options: [],
        config: { required: false, readOnly: false, showInList: true, isFilterable: true, isSortable: true },
      })) }],
    });
    api.getPagedRecords.mockResolvedValue({ items: [], totalCount: 0, currentPage: 1, pagesSize: 20 });
    const userView = (name: string) => ({
      id: 1, name: text(name), shared: false, isOwner: true, columns: [2], sort: [], isDefault: true, staleFieldIds: [],
    });
    api.getViews.mockImplementation(async () => [userView(`view-${useIdentityStore().userId}`)]);
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/dynamic/:moduleKey', component: ListPage }] });
    await router.push('/dynamic/m');
    const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
    const w = mount(MainLayout, {
      global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], pinia, router, i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
    });
    await flushPromises();
    expect(w.text()).toContain('view-admin');

    (w.vm as unknown as { switchUser: (id: string) => void }).switchUser('sales01');
    await flushPromises();
    expect(api.getViews).toHaveBeenCalledTimes(2);
    expect(w.text()).toContain('view-sales01');
    expect(w.text()).not.toContain('view-admin');
  });

  it('MainLayout hides the designer menu without designer read', async () => {
    const pinia = createPinia();
    setActivePinia(pinia);
    let designerRead = false;
    http.defaults.adapter = async (config) => {
      const data = config.url === '/me' ? me(designerRead) : config.url === '/users' ? USERS : [];
      return { data, status: 200, statusText: 'OK', headers: {}, config };
    };
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', component: { template: '<i/>' } }] });
    const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
    const mountIt = async () => {
      const w = mount(MainLayout, {
        global: { plugins: [[quasar.Quasar, { components: quasar }], pinia, router, i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
      });
      await flushPromises();
      return w;
    };
    const hidden = await mountIt();
    expect(hidden.text()).not.toContain(zhTW.menu.dynamicModules);
    expect(hidden.text()).not.toContain(zhTW.menu.system);
    designerRead = true;
    const shown = await mountIt();
    expect(shown.text()).toContain(zhTW.menu.dynamicModules);
    expect(shown.text()).toContain(zhTW.menu.system);
  });

  it('clears the query cache when the stored identity falls back to the first API user', async () => {
    localStorage.setItem('dynamic.userId', 'ghost');
    const pinia = createPinia();
    setActivePinia(pinia);
    http.defaults.adapter = async (config) => ({
      data: config.url === '/me' ? me(true) : config.url === '/users' ? USERS : [], status: 200, statusText: 'OK', headers: {}, config,
    });
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', component: { template: '<i/>' } }] });
    const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
    const queryClient = new QueryClient();
    queryClient.setQueryData(['schema', 'stale'], { stale: true });
    mount(MainLayout, {
      global: { plugins: [[quasar.Quasar, { components: quasar }], pinia, router, i18n, [VueQueryPlugin, { queryClient }]] },
    });
    await flushPromises();
    expect(useIdentityStore().userId).toBe('admin');
    expect(queryClient.getQueryData(['schema', 'stale'])).toBeUndefined();
  });
});
