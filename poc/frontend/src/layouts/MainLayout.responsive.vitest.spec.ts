import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import enUS from 'src/i18n/en-US.json';
import zhTW from 'src/i18n/zh-TW.json';
import { http } from 'src/services/api/http';
import MainLayout from './MainLayout.vue';

const USERS = [{ userId: 'admin', displayName: 'admin' }];
const ME = { userId: 'admin', displayName: 'admin', isSystemAdmin: false, groups: [], designer: { read: false, add: false, edit: false, delete: false } };

/** 手機 390、平板 820、筆電 1366、桌機 1920；Quasar 的 md 斷點是 1024，低於它選單是覆蓋式抽屜。 */
async function mountAtWidth(width: number, locale: 'zh-TW' | 'en-US' = 'zh-TW') {
  window.innerWidth = width;
  // Quasar 的 Screen 是單例，只在 resize 時重新量測（有 50 毫秒的節流）
  window.dispatchEvent(new Event('resize'));
  await new Promise((r) => setTimeout(r, 80));
  const pinia = createPinia();
  setActivePinia(pinia);
  http.defaults.adapter = async (config) => ({
    data: config.url === '/me' ? ME : config.url === '/users' ? USERS : config.url === '/notifications' ? { items: [], unreadCount: 3 } : [], status: 200, statusText: 'OK', headers: {}, config,
  });
  const stub = { template: '<i/>' };
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', component: stub }, { path: '/reports', component: stub }] });
  await router.push('/');
  const i18n = createI18n({ legacy: false, locale, messages: { 'zh-TW': zhTW, 'en-US': enUS } });
  const w = mount(MainLayout, {
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar }], pinia, router, i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
  });
  await flushPromises();
  return { w, router };
}

/** 覆蓋式抽屜的遮罩：收合時仍在 DOM 但有 hidden 類別。 */
const backdrop = () => document.querySelector<HTMLElement>('.q-drawer__backdrop');
const drawerOpen = () => { const b = backdrop(); return b !== null && !b.classList.contains('hidden'); };

describe('MainLayout 響應式選單', () => {
  const original = window.innerWidth;
  beforeEach(() => { vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} }); });
  afterEach(() => { window.innerWidth = original; document.body.innerHTML = ''; });

  it.each([[390], [820]])('%ipx（手機、平板）：預設收合，有帶無障礙標籤的 ☰ 鈕', async (width) => {
    const { w } = await mountAtWidth(width);
    expect(drawerOpen()).toBe(false);
    const toggle = w.find('[data-testid="menu-toggle"]');
    expect(toggle.exists()).toBe(true);
    expect(toggle.attributes('aria-label')).toBe(zhTW.menu.toggle);
    w.unmount();
  });

  it.each([[1366], [1920]])('%ipx（筆電、桌機）：選單常駐，沒有 ☰ 鈕、沒有遮罩', async (width) => {
    const { w } = await mountAtWidth(width);
    expect(w.find('[data-testid="menu-toggle"]').exists()).toBe(false);
    expect(w.find('.q-drawer--standard').exists()).toBe(true);
    expect(drawerOpen()).toBe(false);
    w.unmount();
  });

  // jsdom 不排版，量不到 scrollWidth；改驗會撐寬頁首的東西：鈴鐺只有圖示（沒有文字標籤），badge 浮在圖示上不佔寬
  it.each([[390], [820], [1366], [1920]])('%ipx：頁首有通知鈴鐺，只有圖示，未讀數是浮動 badge', async (width) => {
    const { w } = await mountAtWidth(width);
    const bell = w.get('[data-testid="notification-bell"]');
    expect(bell.text()).toBe('notifications3');
    expect(bell.classes()).toContain('q-btn--round');
    expect(bell.get('[data-testid="notification-badge"]').classes()).toContain('q-badge--floating');
    expect(bell.attributes('aria-label')).toBe(zhTW.notifications.title);
    w.unmount();
  });

  it('390px 英文介面：「變更密碼」只有圖示，帶 aria-label 與 tooltip，不擠壓品牌名', async () => {
    const { w } = await mountAtWidth(390, 'en-US');
    const link = w.get('[data-testid="change-password-link"]');
    expect(link.text()).not.toContain(enUS.auth.changePassword);
    expect(link.attributes('aria-label')).toBe(enUS.auth.changePassword);
    expect(link.findComponent(quasar.QTooltip).exists()).toBe(true);
    w.unmount();
  });

  it('1366px 英文介面：「變更密碼」維持文字', async () => {
    const { w } = await mountAtWidth(1366, 'en-US');
    expect(w.get('[data-testid="change-password-link"]').text()).toContain(enUS.auth.changePassword);
    w.unmount();
  });

  it('☰ 鈕開啟覆蓋式抽屜；點選項目後關閉', async () => {
    const { w, router } = await mountAtWidth(390);
    await w.find('[data-testid="menu-toggle"]').trigger('click');
    expect(drawerOpen()).toBe(true);
    await w.find('[data-testid="reports-menu"]').trigger('click');
    await flushPromises();
    expect(router.currentRoute.value.path).toBe('/reports');
    expect(drawerOpen()).toBe(false);
    w.unmount();
  });

  it('點目前所在頁的項目（不會換路由）也關閉', async () => {
    const { w } = await mountAtWidth(390);
    await w.find('[data-testid="menu-toggle"]').trigger('click');
    await w.find('[data-testid="home-menu"]').trigger('click');
    await flushPromises();
    expect(drawerOpen()).toBe(false);
    w.unmount();
  });

  it('點遮罩關閉', async () => {
    const { w } = await mountAtWidth(390);
    await w.find('[data-testid="menu-toggle"]').trigger('click');
    backdrop()!.click();
    await flushPromises();
    expect(drawerOpen()).toBe(false);
    w.unmount();
  });

  it('按 Esc 關閉', async () => {
    const { w } = await mountAtWidth(820);
    await w.find('[data-testid="menu-toggle"]').trigger('click');
    expect(drawerOpen()).toBe(true);
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    await flushPromises();
    expect(drawerOpen()).toBe(false);
    w.unmount();
  });

  it('無障礙標籤隨語系：英文版也有', async () => {
    const { w } = await mountAtWidth(390, 'en-US');
    expect(w.find('[data-testid="menu-toggle"]').attributes('aria-label')).toBe(enUS.menu.toggle);
    w.unmount();
  });
});
