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
const adminMe = (isSystemAdmin: boolean) => ({ ...me(false), isSystemAdmin });

describe('MainLayout menu', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  it('refetches the menu after the session changes user', async () => {
    const pinia = createPinia();
    setActivePinia(pinia);
    useIdentityStore().setSession('admin');
    const seen: string[] = [];
    http.defaults.adapter = async (config) => {
      const user = useIdentityStore().userId;
      let data: unknown = [];
      if (config.url === '/users') data = USERS;
      else if (config.url === '/me') data = me(true);
      else { seen.push(user); data = [{ key: 'm', name: { 'zh-TW': `menu-${user}` } }]; }
      return { data, status: 200, statusText: 'OK', headers: {}, config };
    };
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', component: { template: '<i/>' } }] });
    const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
    const queryClient = new QueryClient();
    const w = mount(MainLayout, {
      global: { plugins: [[quasar.Quasar, { components: quasar }], pinia, router, i18n, [VueQueryPlugin, { queryClient }]] },
    });
    await flushPromises();
    expect(seen).toEqual(['admin']);
    expect(w.text()).toContain('menu-admin');

    useIdentityStore().setSession('sales01');
    queryClient.clear();
    await flushPromises();
    expect(seen).toEqual(['admin', 'sales01']);
    expect(w.text()).toContain('menu-sales01');
  });

  it.each([[true], [false]])('demo-data menu follows designer.read (%s)', async (read) => {
    const pinia = createPinia();
    setActivePinia(pinia);
    http.defaults.adapter = async (config) => ({
      data: config.url === '/me' ? me(read) : config.url === '/users' ? USERS : [], status: 200, statusText: 'OK', headers: {}, config,
    });
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', component: { template: '<i/>' } }] });
    const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
    const w = mount(MainLayout, {
      global: { plugins: [[quasar.Quasar, { components: quasar }], pinia, router, i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
    });
    await flushPromises();
    expect(w.find('[data-testid="demo-data-menu"]').exists()).toBe(read);
  });

  it('menu shows reports to every signed-in user, without designer or admin rights', async () => {
    const pinia = createPinia();
    setActivePinia(pinia);
    http.defaults.adapter = async (config) => ({
      data: config.url === '/me' ? me(false) : config.url === '/users' ? USERS : [], status: 200, statusText: 'OK', headers: {}, config,
    });
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', component: { template: '<i/>' } }] });
    const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
    const w = mount(MainLayout, {
      global: { plugins: [[quasar.Quasar, { components: quasar }], pinia, router, i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
    });
    await flushPromises();
    expect(w.find('[data-testid="reports-menu"]').exists()).toBe(true);
    expect(w.find('[data-testid="reports-menu"]').text()).toContain('我的報表');
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
    expect(w.findAll('.q-item__label--header').map((h) => h.text())).toEqual(['自訂功能', 'Alpha', 'Zeta', '報表']);
  });

  it('refetches views and re-picks the default view after the session changes user', async () => {
    const pinia = createPinia();
    setActivePinia(pinia);
    useIdentityStore().setSession('admin');
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
    const queryClient = new QueryClient();
    const w = mount(MainLayout, {
      global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], pinia, router, i18n, [VueQueryPlugin, { queryClient }]] },
    });
    await flushPromises();
    expect(w.text()).toContain('view-admin');

    useIdentityStore().setSession('sales01');
    queryClient.clear();
    await flushPromises();
    expect(api.getViews).toHaveBeenCalledTimes(2);
    expect(w.text()).toContain('view-sales01');
    expect(w.text()).not.toContain('view-admin');
  });

  it('users menu follows isSystemAdmin and disappears when me is refetched without it', async () => {
    const pinia = createPinia();
    setActivePinia(pinia);
    let isAdmin = true;
    http.defaults.adapter = async (config) => ({
      data: config.url === '/me' ? adminMe(isAdmin) : config.url === '/users' ? USERS : [], status: 200, statusText: 'OK', headers: {}, config,
    });
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', component: { template: '<i/>' } }] });
    const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
    const queryClient = new QueryClient();
    const w = mount(MainLayout, {
      global: { plugins: [[quasar.Quasar, { components: quasar }], pinia, router, i18n, [VueQueryPlugin, { queryClient }]] },
    });
    await flushPromises();
    expect(w.find('[data-testid="users-menu"]').exists()).toBe(true);
    expect(w.find('[data-testid="designer-menu"]').exists()).toBe(false);
    expect(w.text()).toContain(zhTW.menu.system);
    isAdmin = false;
    await queryClient.invalidateQueries({ queryKey: ['me'] });
    await flushPromises();
    expect(w.find('[data-testid="users-menu"]').exists()).toBe(false);
    expect(w.text()).not.toContain(zhTW.menu.system);
  });

  it('identity.users follows /users after invalidating ["users"]', async () => {
    const pinia = createPinia();
    setActivePinia(pinia);
    let list = USERS;
    http.defaults.adapter = async (config) => ({
      data: config.url === '/me' ? me(false) : config.url === '/users' ? list : [], status: 200, statusText: 'OK', headers: {}, config,
    });
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', component: { template: '<i/>' } }] });
    const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
    const queryClient = new QueryClient();
    mount(MainLayout, {
      global: { plugins: [[quasar.Quasar, { components: quasar }], pinia, router, i18n, [VueQueryPlugin, { queryClient }]] },
    });
    await flushPromises();
    expect(useIdentityStore().users.map((u) => u.userId)).toEqual(['admin', 'sales01']);
    list = [...USERS, { userId: 'new01', displayName: 'new01' }];
    await queryClient.invalidateQueries({ queryKey: ['users'] });
    await flushPromises();
    expect(useIdentityStore().users.map((u) => u.userId)).toEqual(['admin', 'sales01', 'new01']);
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
    expect(shown.find('.q-toolbar__title').text()).toBe('Formwright');
  });

  describe('account area', () => {
    const logoutSetup = async (meExtra: Record<string, unknown> = {}) => {
      const pinia = createPinia();
      setActivePinia(pinia);
      useIdentityStore().setSession('admin');
      const calls: string[] = [];
      http.defaults.adapter = async (config) => {
        calls.push(`${config.method} ${config.url}`);
        const data = config.url === '/me' ? { ...me(false), displayName: '管理員小明', ...meExtra } : config.url === '/users' ? USERS : [];
        return { data, status: 200, statusText: 'OK', headers: {}, config };
      };
      const router = createRouter({
        history: createMemoryHistory(),
        routes: [{ path: '/', component: { template: '<i/>' } }, { path: '/login', component: { template: '<i/>' } }, { path: '/change-password', component: { template: '<i/>' } }],
      });
      await router.push('/');
      const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
      const queryClient = new QueryClient();
      const w = mount(MainLayout, {
        attachTo: document.body,
        global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], pinia, router, i18n, [VueQueryPlugin, { queryClient }]] },
      });
      await flushPromises();
      return { w, router, queryClient, calls };
    };

    it('has no identity select; shows the user name, change-password and logout', async () => {
      const { w } = await logoutSetup();
      expect(w.find('[data-testid="identity-select"]').exists()).toBe(false);
      expect(w.find('[data-testid="current-user"]').text()).toContain('管理員小明');
      expect(w.find('[data-testid="change-password-link"]').exists()).toBe(true);
      expect(w.find('[data-testid="logout"]').exists()).toBe(true);
      w.unmount();
    });

    it('hides the change-password link for demo accounts', async () => {
      const { w } = await logoutSetup({ isDemoAccount: true });
      expect(w.find('[data-testid="logout"]').exists()).toBe(true);
      expect(w.find('[data-testid="change-password-link"]').exists()).toBe(false);
      w.unmount();
    });

    it('change-password link goes to /change-password', async () => {
      const { w, router } = await logoutSetup();
      await w.find('[data-testid="change-password-link"]').trigger('click');
      await flushPromises();
      expect(router.currentRoute.value.path).toBe('/change-password');
      w.unmount();
    });

    it('logout calls the API, clears cache and store, and goes to /login', async () => {
      const { w, router, queryClient, calls } = await logoutSetup();
      queryClient.setQueryData(['stale'], 1);
      await w.find('[data-testid="logout"]').trigger('click');
      await flushPromises();
      expect(calls).toContain('post /auth/logout');
      expect(queryClient.getQueryData(['stale'])).toBeUndefined();
      expect(useIdentityStore().userId).toBe('');
      expect(router.currentRoute.value.path).toBe('/login');
      w.unmount();
    });

    it('logout failure keeps the session, the cache and the page, and shows an error', async () => {
      const { w, router, queryClient } = await logoutSetup();
      const ok = http.defaults.adapter as (c: never) => Promise<unknown>;
      http.defaults.adapter = (async (config: { url?: string }) => {
        if (config.url === '/auth/logout') return Promise.reject({ config, message: 'Network Error' });
        return ok(config as never);
      }) as never;
      queryClient.setQueryData(['stale'], 1);
      await w.find('[data-testid="logout"]').trigger('click');
      await flushPromises();
      expect(useIdentityStore().userId).toBe('admin');
      expect(queryClient.getQueryData(['stale'])).toBe(1);
      expect(router.currentRoute.value.path).toBe('/');
      expect(w.find('[data-testid="logout-error"]').text()).toContain(zhTW.auth.logoutFailed);
      w.unmount();
    });
  });
});
