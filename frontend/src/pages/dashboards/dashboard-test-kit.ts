import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { vi } from 'vitest';
import { h, type Component } from 'vue';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter, RouterView } from 'vue-router';
import type { CardResult, DashboardCard, DashboardDetail, DashboardList } from 'src/domain/dynamic/dashboard-types';
import zhTW from 'src/i18n/zh-TW.json';
import { http } from 'src/services/api/http';
import { useIdentityStore } from 'src/stores/use-identity-store';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });

export const card = (id: string, reportId = 1, over: Partial<DashboardCard> = {}): DashboardCard => ({
  id, reportId, viz: 'table', x: 0, y: 0, w: 6, h: 4, options: { sortDesc: false, cumulative: false }, ...over,
});

export const okResult = (over: Partial<CardResult> = {}): CardResult => ({
  state: 'ok', rowMeaning: '每列一個狀態', dedupedNote: false, basisDate: '2026-11-05',
  columns: [{ key: 'g', label: '狀態', role: 'group', valueType: 'text' }, { key: 'm', label: '件數', role: 'measure', valueType: 'number' }],
  groups: [{ groupKeys: ['a'], groupLabels: ['進行中'], values: { m: 3 } }], total: { m: 3 },
  ...over,
} as CardResult);

export const detail = (id: number, over: Partial<DashboardDetail> = {}): DashboardDetail => ({
  id, name: `儀表板${id}`, kind: 'assigned', version: 1, adjusted: false, hasUpdate: false,
  layout: { cards: [card('c1')] }, reportNames: { 1: '訂單狀態' }, ...over,
});

export interface Call { method: string; url: string; body: unknown; params: unknown }
export interface World {
  list?: DashboardList | { status: number };
  details?: Record<number, DashboardDetail>;
  /** cardId → 結果；Error 代表該張回 500 */
  cards?: Record<string, CardResult | Error>;
  me?: Record<string, unknown>;
  /** 其他 URL 的自訂回應，key 是 `METHOD /path`；值帶 __status 時回該狀態碼的錯誤 */
  extra?: Record<string, unknown | ((c: Call) => unknown)>;
}

export const meOf = (o: { admin?: boolean } = {}) => ({
  userId: 'u', displayName: 'Alice', isSystemAdmin: o.admin ?? false, groups: [],
  designer: { read: false, add: false, edit: false, delete: false },
});

/** 依 method + url 回應；所有呼叫記在 calls，供斷言「有沒有送 PUT」。 */
export function installApi(world: World) {
  const calls: Call[] = [];
  http.defaults.adapter = async (config) => {
    const method = (config.method ?? 'get').toUpperCase();
    const url = config.url ?? '';
    const body = typeof config.data === 'string' ? JSON.parse(config.data) : config.data;
    const call = { method, url, body, params: config.params };
    calls.push(call);
    const ok = (data: unknown) => ({ data, status: 200, statusText: 'OK', headers: {}, config });
    const fail = (status: number) => Promise.reject({ response: { status, data: {} }, isAxiosError: true });
    const extra = world.extra?.[`${method} ${url}`];
    if (extra !== undefined) {
      const value = typeof extra === 'function' ? (extra as (c: Call) => unknown)(call) : extra;
      const status = (value as { __status?: number } | null)?.__status;
      return status ? fail(status) : ok(value);
    }
    if (url === '/me') return ok(world.me ?? meOf());
    if (url === '/menu') return ok([]);
    if (method === 'GET' && url === '/dashboards') {
      if (world.list && 'status' in world.list) return fail(world.list.status);
      return ok(world.list ?? { assigned: [], mine: [], sharedReports: [] });
    }
    const run = url.match(/^\/dashboards\/(\d+)\/cards\/([^/]+)\/run$/);
    if (run) {
      const r = world.cards?.[decodeURIComponent(run[2])] ?? okResult();
      return r instanceof Error ? fail(500) : ok(r);
    }
    const one = url.match(/^\/dashboards\/(\d+)$/);
    if (method === 'GET' && one) {
      const d = world.details?.[Number(one[1])];
      return d ? ok(d) : fail(404);
    }
    const put = url.match(/^\/dashboards\/(\d+)\/(layout|restore)$/);
    if (put) {
      const d = world.details?.[Number(put[1])];
      if (!d) return fail(404);
      if (put[2] === 'layout') return ok({ ...d, layout: (body as { layout: DashboardDetail['layout'] }).layout, adjusted: true });
      return ok({ ...d, adjusted: false, hasUpdate: false });
    }
    if (url === '/reports') return ok([]);
    return ok([]);
  };
  return calls;
}

export interface MountOpts { route?: string; page: Component; userId?: string; routes?: { path: string; component: Component }[] }

export async function mountPage(world: World, opts: MountOpts) {
  setActivePinia(createPinia());
  useIdentityStore().setSession(opts.userId ?? 'u');
  const calls = installApi(world);
  const stub = { template: '<i/>' };
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', component: opts.page },
      { path: '/dashboards/:id(\\d+)?', component: opts.page },
      { path: '/system/dashboards', component: opts.page },
      { path: '/reports', component: stub },
      { path: '/system/:rest(.*)', component: stub },
      ...(opts.routes ?? []),
    ],
  });
  await router.push(opts.route ?? '/dashboards');
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount({ render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(RouterView))) }, {
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, plugins: { Notify: quasar.Notify } }], router, i18n, [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]] },
  });
  await flushPromises();
  return { w, router, calls };
}

export const q = (id: string) => document.querySelector(`[data-testid="${id}"]`);
export const qa = (id: string) => [...document.querySelectorAll(`[data-testid="${id}"]`)];
export async function click(id: string) {
  (q(id) as HTMLElement).click();
  await flushPromises();
}
