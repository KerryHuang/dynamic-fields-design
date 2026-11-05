import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter, RouterView } from 'vue-router';
import zhTW from 'src/i18n/zh-TW.json';
import { http } from 'src/services/api/http';
import HomePage from './HomePage.vue';

const me = (designerRead: boolean) => ({
  userId: 'u', displayName: 'u', isSystemAdmin: false, groups: [],
  designer: { read: designerRead, add: false, edit: false, delete: false },
});

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });

async function run(designerRead: boolean, menu: unknown[]) {
  setActivePinia(createPinia());
  http.defaults.adapter = async (config) => ({
    data: config.url === '/me' ? me(designerRead) : menu, status: 200, statusText: 'OK', headers: {}, config,
  });
  const stub = { template: '<i/>' };
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', component: HomePage },
      { path: '/system/dynamic-modules', component: stub },
      { path: '/dynamic/:moduleKey', component: stub },
    ],
  });
  await router.push('/');
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount({ render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(RouterView))) }, {
    global: { plugins: [[quasar.Quasar, { components: quasar }], router, i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
  });
  await flushPromises();
  return { router, w };
}

describe('HomePage', () => {
  it('HomePage redirects to the designer when allowed, else to the first menu module, else shows forbidden', async () => {
    const menu = [{ key: 'orders', name: { 'zh-TW': 'o' } }, { key: 'b', name: { 'zh-TW': 'b' } }];
    expect((await run(true, menu)).router.currentRoute.value.path).toBe('/system/dynamic-modules');
    expect((await run(false, menu)).router.currentRoute.value.path).toBe('/dynamic/orders');
    const none = await run(false, []);
    expect(none.router.currentRoute.value.path).toBe('/');
    expect(none.w.text()).toContain(zhTW.dynamic.forbidden);
  });

  it('does not flash forbidden while the menu is pending after me errored, then redirects', async () => {
    setActivePinia(createPinia());
    let releaseMenu!: () => void;
    const gate = new Promise<void>((r) => { releaseMenu = r; });
    http.defaults.adapter = async (config) => {
      if (config.url === '/me') return Promise.reject({ status: 401 });
      await gate;
      return { data: [{ key: 'orders', name: { 'zh-TW': 'o' } }], status: 200, statusText: 'OK', headers: {}, config };
    };
    const stub = { template: '<i/>' };
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/', component: HomePage }, { path: '/dynamic/:moduleKey', component: stub }],
    });
    await router.push('/');
    const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
    const w = mount({ render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(RouterView))) }, {
      global: { plugins: [[quasar.Quasar, { components: quasar }], router, i18n, [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]] },
    });
    await flushPromises();
    expect(w.text()).not.toContain(zhTW.dynamic.forbidden);
    releaseMenu();
    await flushPromises();
    expect(router.currentRoute.value.path).toBe('/dynamic/orders');
  });
});
