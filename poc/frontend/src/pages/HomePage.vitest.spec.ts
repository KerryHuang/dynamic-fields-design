import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter, RouterView } from 'vue-router';
import zhTW from 'src/i18n/zh-TW.json';
import { http } from 'src/services/api/http';
import { useIdentityStore } from 'src/stores/use-identity-store';
import HomePage from './HomePage.vue';

const me = (o: { designerRead?: boolean; admin?: boolean } = {}) => ({
  userId: 'u', displayName: 'Alice', isSystemAdmin: o.admin ?? false, groups: [],
  designer: { read: o.designerRead ?? false, add: false, edit: false, delete: false },
});
const mod = (key: string, section?: string, icon?: string) => ({ key, name: { 'zh-TW': `name-${key}` }, section, icon });

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
afterEach(() => localStorage.clear());

async function run(opts: { me?: ReturnType<typeof me>; menu?: unknown[]; recent?: string[]; gate?: Promise<void>; menuStatus?: number } = {}) {
  setActivePinia(createPinia());
  useIdentityStore().setSession('u');
  if (opts.recent) localStorage.setItem('formwright:recent-modules:u', JSON.stringify(opts.recent));
  http.defaults.adapter = async (config) => {
    if (config.url === '/me') return { data: opts.me ?? me(), status: 200, statusText: 'OK', headers: {}, config };
    if (opts.gate) await opts.gate;
    if (opts.menuStatus) return Promise.reject({ status: opts.menuStatus });
    return { data: opts.menu ?? [], status: 200, statusText: 'OK', headers: {}, config };
  };
  const stub = { template: '<i/>' };
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', component: HomePage },
      { path: '/reports', component: stub },
      { path: '/system/:rest(.*)', component: stub },
      { path: '/dynamic/:moduleKey', component: stub },
    ],
  });
  await router.push('/');
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount({ render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(RouterView))) }, {
    global: { plugins: [[quasar.Quasar, { components: quasar }], router, i18n, [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]] },
  });
  await flushPromises();
  return { router, w };
}

describe('HomePage', () => {
  it('stays on the home page for everyone and greets the user by name', async () => {
    for (const m of [me({ designerRead: true }), me()]) {
      const { router, w } = await run({ me: m, menu: [mod('orders')] });
      expect(router.currentRoute.value.path).toBe('/');
      expect(w.get('[data-testid="home-welcome"]').text()).toContain('Alice');
      expect(w.get('[data-testid="home-welcome"]').text()).toContain('Formwright');
    }
  });

  it('shows module cards grouped by menu section, linking to the module', async () => {
    const { w } = await run({ menu: [mod('orders'), mod('crm1', '業務', 'people'), mod('crm2', '業務')] });
    const groups = w.findAll('[data-testid="home-app-group"]');
    expect(groups.map((g) => g.get('[data-testid="home-group-title"]').text())).toEqual([zhTW.menu.custom, '業務']);
    const cards = w.findAll('[data-testid="home-app-card"]');
    expect(cards.map((c) => c.get('.q-item__section--main').text())).toEqual(['name-orders', 'name-crm1', 'name-crm2']);
    expect(cards.map((c) => c.attributes('href'))).toEqual(['/dynamic/orders', '/dynamic/crm1', '/dynamic/crm2']);
  });

  it('lists recent modules (max 5, only ones still in the menu), most recent first', async () => {
    const menu = ['a', 'b', 'c', 'd', 'e', 'f'].map((k) => mod(k));
    const { w } = await run({ menu, recent: ['f', 'gone', 'e', 'd', 'c', 'b', 'a'] });
    const hrefs = w.findAll('[data-testid="home-recent-item"]').map((c) => c.attributes('href'));
    expect(hrefs).toEqual(['/dynamic/f', '/dynamic/e', '/dynamic/d', '/dynamic/c', '/dynamic/b']);
  });

  it('hides the recent section when nothing was opened or storage is unavailable', async () => {
    expect((await run({ menu: [mod('a')] })).w.find('[data-testid="home-recent"]').exists()).toBe(false);
    const spy = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('blocked'); });
    const { w } = await run({ menu: [mod('a')], recent: ['a'] });
    spy.mockRestore();
    expect(w.find('[data-testid="home-recent"]').exists()).toBe(false);
    expect(w.findAll('[data-testid="home-app-card"]')).toHaveLength(1);
  });

  it('links to my reports', async () => {
    const { w } = await run({ menu: [mod('a')] });
    expect(w.get('[data-testid="home-reports"]').attributes('href')).toBe('/reports');
  });

  it('shows system shortcuts only to people with the matching permission', async () => {
    const links = async (m: ReturnType<typeof me>) =>
      (await run({ me: m, menu: [mod('a')] })).w.findAll('[data-testid="home-system-link"]').map((l) => l.attributes('href'));
    expect(await links(me())).toEqual([]);
    expect((await run({ menu: [mod('a')] })).w.find('[data-testid="home-system"]').exists()).toBe(false);
    expect(await links(me({ admin: true }))).toEqual(['/system/users', '/system/groups', '/system/appearance']);
    expect(await links(me({ designerRead: true }))).toEqual(['/system/dynamic-modules', '/system/demo-data']);
  });

  it('shows a friendly empty state without modules, but not while the menu is still loading', async () => {
    let release!: () => void;
    const gate = new Promise<void>((r) => { release = r; });
    const pending = await run({ gate, menu: [] });
    expect(pending.w.find('[data-testid="home-empty"]').exists()).toBe(false);
    release();
    await flushPromises();
    expect(pending.w.get('[data-testid="home-empty"]').text()).toContain(zhTW.home.noModules);
    expect(pending.w.find('[data-testid="home-reports"]').exists()).toBe(true);
  });

  it('shows the forbidden notice when the menu cannot be loaded', async () => {
    const { w } = await run({ menuStatus: 500 });
    expect(w.text()).toContain(zhTW.dynamic.forbidden);
  });
});
