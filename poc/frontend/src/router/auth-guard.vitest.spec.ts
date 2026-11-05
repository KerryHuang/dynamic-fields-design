import { QueryClient } from '@tanstack/vue-query';
import { createPinia, setActivePinia } from 'pinia';
import { beforeEach, describe, expect, it } from 'vitest';
import { createMemoryHistory, createRouter, type Router } from 'vue-router';
import { http } from 'src/services/api/http';
import { useIdentityStore } from 'src/stores/use-identity-store';
import { handleUnauthorized, installAuthGuard, safeRedirect } from './auth-guard';

const page = { template: '<i/>' };
const routes = [
  { path: '/', component: page },
  { path: '/login', component: page, meta: { public: true } },
  { path: '/change-password', component: page },
  { path: '/dynamic/:moduleKey', component: page },
];

let meResponse: { status: number; data: unknown };
let meCalls: number;

function setup() {
  setActivePinia(createPinia());
  meCalls = 0;
  http.defaults.adapter = async (config) => {
    if (config.url === '/me') meCalls++;
    const r = meResponse;
    if (r.status >= 400) return Promise.reject({ config, response: r });
    return { data: r.data, status: r.status, statusText: 'OK', headers: {}, config };
  };
  const router = createRouter({ history: createMemoryHistory(), routes });
  installAuthGuard(router);
  return router;
}

describe('auth guard', () => {
  beforeEach(() => {
    meResponse = { status: 401, data: { message: '請先登入' } };
  });

  it('anonymous visitor is sent to /login with the target as redirect', async () => {
    const router = setup();
    await router.push('/dynamic/order');
    expect(router.currentRoute.value.path).toBe('/login');
    expect(router.currentRoute.value.query.redirect).toBe('/dynamic/order');
  });

  it('keeps the query string of the target in redirect', async () => {
    const router = setup();
    await router.push('/dynamic/order?q=a');
    expect(router.currentRoute.value.query.redirect).toBe('/dynamic/order?q=a');
  });

  it('loads the session from /me once and lets a logged-in user through', async () => {
    meResponse = { status: 200, data: { userId: 'admin', mustChangePassword: false } };
    const router = setup();
    await router.push('/dynamic/order');
    await router.push('/');
    expect(router.currentRoute.value.path).toBe('/');
    expect(useIdentityStore().userId).toBe('admin');
    expect(meCalls).toBe(1);
  });

  it('logged-in user visiting /login goes home', async () => {
    meResponse = { status: 200, data: { userId: 'admin', mustChangePassword: false } };
    const router = setup();
    await router.push('/login');
    expect(router.currentRoute.value.path).toBe('/');
  });

  it('mustChangePassword forces /change-password for every other page', async () => {
    meResponse = { status: 200, data: { userId: 'admin', mustChangePassword: true } };
    const router = setup();
    await router.push('/dynamic/order');
    expect(router.currentRoute.value.path).toBe('/change-password');
    await router.push('/change-password');
    expect(router.currentRoute.value.path).toBe('/change-password');
  });

  it('anonymous visitor may open /login', async () => {
    const router = setup();
    await router.push('/login');
    expect(router.currentRoute.value.path).toBe('/login');
  });
});

describe('handleUnauthorized (session expiry mid-edit)', () => {
  const loggedIn = async (path: string): Promise<{ router: Router; qc: QueryClient }> => {
    meResponse = { status: 200, data: { userId: 'admin', mustChangePassword: false } };
    const router = setup();
    await router.push(path);
    const qc = new QueryClient();
    qc.setQueryData(['x'], 1);
    return { router, qc };
  };

  it('clears store and cache and goes to /login?redirect=<current path>', async () => {
    const { router, qc } = await loggedIn('/dynamic/order?tab=1');
    handleUnauthorized(router, qc);
    await new Promise((r) => setTimeout(r));
    expect(useIdentityStore().userId).toBe('');
    expect(qc.getQueryData(['x'])).toBeUndefined();
    expect(router.currentRoute.value.path).toBe('/login');
    expect(router.currentRoute.value.query.redirect).toBe('/dynamic/order?tab=1');
  });

  it('is a no-op when nobody is logged in (parallel 401s, or /me during the first navigation)', async () => {
    const router = setup();
    await router.push('/login');
    handleUnauthorized(router, new QueryClient());
    await new Promise((r) => setTimeout(r));
    expect(router.currentRoute.value.fullPath).toBe('/login');
  });
});

describe('safeRedirect', () => {
  it.each([
    ['//evil.example'], ['/\\evil.example'], ['https://evil.example'], ['evil'], [''], [undefined],
  ])('rejects %s', (p) => expect(safeRedirect(p)).toBe('/'));

  it.each([['/dynamic/order'], ['/dynamic/order?q=1'], ['/']])('accepts %s', (p) => expect(safeRedirect(p)).toBe(p));
});
