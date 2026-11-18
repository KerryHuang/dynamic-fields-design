import { afterAll, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { createApp } from 'vue';
import { createMemoryHistory, createRouter } from 'vue-router';

const notify = vi.hoisted(() => vi.fn());
vi.mock('quasar', async (orig) => ({ ...(await orig<typeof import('quasar')>()), Notify: { create: notify } }));

import boot from './error-reporter';

const fetchMock = vi.fn(async () => new Response(null, { status: 204 }));
const sent = () => fetchMock.mock.calls.map((c) => JSON.parse((c as unknown as [string, RequestInit])[1].body as string));

describe('boot error-reporter', () => {
  const app = createApp({});
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/dynamic/:moduleKey', component: {} }] });

  beforeAll(async () => {
    vi.stubGlobal('fetch', fetchMock);
    vi.spyOn(console, 'error').mockImplementation(() => {});
    await (boot as unknown as (ctx: object) => unknown)({ app, router });
    await router.push('/dynamic/orders?q=1');
  });
  afterAll(() => vi.unstubAllGlobals());
  beforeEach(() => { fetchMock.mockClear(); notify.mockClear(); });

  it('Vue errorHandler 回報 vue，保留 console 輸出，帶路由樣板', () => {
    app.config.errorHandler!(new Error('vue boom'), null, '');
    expect(console.error).toHaveBeenCalled();
    expect(sent()).toEqual([expect.objectContaining({ kind: 'vue', message: 'vue boom', route: '/dynamic/:moduleKey', moduleKey: 'orders' })]);
  });

  it('window error 與 unhandledrejection；API 失敗的 rejection 不回報', () => {
    window.dispatchEvent(new ErrorEvent('error', { error: new Error('win boom'), message: 'win boom' }));
    const rejection = (reason: unknown) => Object.assign(new Event('unhandledrejection'), { reason });
    window.dispatchEvent(rejection(new Error('promise boom')));
    window.dispatchEvent(rejection({ status: 500, message: 'api' }));
    expect(sent().map((r) => [r.kind, r.message])).toEqual([['window', 'win boom'], ['promise', 'promise boom']]);
  });

  it('程式塊載入失敗歸類為 chunk，更新提示只顯示一次', () => {
    const chunk = new TypeError('Failed to fetch dynamically imported module: /assets/ListPage-abc12345.js');
    window.dispatchEvent(Object.assign(new Event('unhandledrejection'), { reason: chunk }));
    window.dispatchEvent(Object.assign(new Event('vite:preloadError'), { payload: new Error('Unable to preload CSS for /assets/x.css') }));
    expect(sent().map((r) => r.kind)).toEqual(['chunk', 'chunk']);
    expect(notify).toHaveBeenCalledTimes(1);
    expect(notify).toHaveBeenCalledWith(expect.objectContaining({ type: 'warning', message: '系統已更新，請重新整理頁面', timeout: 0 }));
  });
});
