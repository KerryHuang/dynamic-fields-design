import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import enUS from 'src/i18n/en-US.json';
import th from 'src/i18n/th.json';
import vi_ from 'src/i18n/vi.json';
import zhTW from 'src/i18n/zh-TW.json';
import { NOTIFICATION_KINDS } from 'src/domain/dynamic/task-types';
import { mockHttp, type Call } from 'src/test-utils/mock-http';
import NotificationBell from './NotificationBell.vue';

let calls: Call[];

const note = (id: number, o: Record<string, unknown> = {}) =>
  ({ id, kind: 'taskAssigned', title: `任務${id}`, link: `/tasks?open=${id + 4}`, read: false, createdTime: '2026-11-10T01:00:00', ...o });

async function mountBell(list: unknown) {
  setActivePinia(createPinia());
  calls = mockHttp((c) => (c.url === '/notifications' ? list : {}));
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/:p(.*)*', component: { render: () => null } }] });
  await router.push('/');
  const push = vi.spyOn(router, 'push');
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(NotificationBell, {
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], createPinia(), router, i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
  });
  await flushPromises();
  return { w, push };
}

const body = (sel: string) => document.body.querySelector<HTMLElement>(sel);

describe('NotificationBell', () => {
  beforeEach(() => { document.body.innerHTML = ''; });

  it('未讀數 3 時 badge 顯示 3，0 時沒有 badge', async () => {
    const a = await mountBell({ items: [note(9)], unreadCount: 3 });
    expect(a.w.get('[data-testid="notification-badge"]').text()).toBe('3');
    a.w.unmount();
    const b = await mountBell({ items: [], unreadCount: 0 });
    expect(b.w.find('[data-testid="notification-badge"]').exists()).toBe(false);
    b.w.unmount();
  });

  it('回應格式不對時當成空清單', async () => {
    const { w } = await mountBell('oops');
    expect(w.find('[data-testid="notification-badge"]').exists()).toBe(false);
    await w.get('[data-testid="notification-bell"]').trigger('click');
    await flushPromises();
    expect(body('[data-testid="notification-empty"]')).not.toBeNull();
    w.unmount();
  });

  it('點一則：POST 標為已讀，再前往它的連結', async () => {
    const { w, push } = await mountBell({ items: [note(9)], unreadCount: 1 });
    await w.get('[data-testid="notification-bell"]').trigger('click');
    await flushPromises();
    const item = body('[data-testid="notification-item-9"]')!;
    expect(item.textContent).toContain(zhTW.notifications.kind.taskAssigned);
    expect(item.textContent).toContain('任務9');
    item.click();
    await flushPromises();
    expect(calls.some((c) => c.method === 'post' && c.url === '/notifications/9/read')).toBe(true);
    expect(push).toHaveBeenCalledWith('/tasks?open=13');
    w.unmount();
  });

  it('每則通知顯示建立時間（YYYY-MM-DD HH:mm）', async () => {
    const { w } = await mountBell({ items: [note(9)], unreadCount: 1 });
    await w.get('[data-testid="notification-bell"]').trigger('click');
    await flushPromises();
    expect(body('[data-testid="notification-time-9"]')!.textContent).toBe(quasar.date.formatDate(new Date('2026-11-10T01:00:00'), 'YYYY-MM-DD HH:mm'));
    w.unmount();
  });

  it('沒有連結的通知只標為已讀；站外連結不前往', async () => {
    const { w, push } = await mountBell({
      items: [note(1, { link: '' }), note(2, { link: '//evil.example/x' }), note(3, { link: '/\\evil.example/x' })], unreadCount: 3,
    });
    await w.get('[data-testid="notification-bell"]').trigger('click');
    await flushPromises();
    body('[data-testid="notification-item-1"]')!.click();
    body('[data-testid="notification-item-2"]')!.click();
    body('[data-testid="notification-item-3"]')!.click();
    await flushPromises();
    expect(calls.filter((c) => c.method === 'post').map((c) => c.url))
      .toEqual(['/notifications/1/read', '/notifications/2/read', '/notifications/3/read']);
    expect(push).not.toHaveBeenCalled();
    w.unmount();
  });

  it('全部標為已讀：POST /notifications/read-all', async () => {
    const { w } = await mountBell({ items: [note(9)], unreadCount: 1 });
    await w.get('[data-testid="notification-bell"]').trigger('click');
    await flushPromises();
    body('[data-testid="notification-read-all"]')!.click();
    await flushPromises();
    expect(calls.some((c) => c.method === 'post' && c.url === '/notifications/read-all')).toBe(true);
    w.unmount();
  });

  it('已讀的通知不加粗；未知種類只顯示標題', async () => {
    const { w } = await mountBell({ items: [note(1, { read: true }), note(2, { kind: 'whatever' })], unreadCount: 1 });
    await w.get('[data-testid="notification-bell"]').trigger('click');
    await flushPromises();
    expect(body('[data-testid="notification-item-1"]')!.classList.contains('text-weight-bold')).toBe(false);
    expect(body('[data-testid="notification-item-2"]')!.classList.contains('text-weight-bold')).toBe(true);
    expect(body('[data-testid="notification-item-2"]')!.textContent).toContain('任務2');
    w.unmount();
  });

  it('approval kinds show their label before the title', async () => {
    const { w } = await mountBell({ items: [note(1, { kind: 'approvalPending', title: 'PO-0001' }), note(2, { kind: 'approvalApproved', title: 'PO-0002' })], unreadCount: 2 });
    await w.get('[data-testid="notification-bell"]').trigger('click');
    await flushPromises();
    expect(body('[data-testid="notification-item-1"]')!.textContent).toContain('待你簽核： PO-0001');
    expect(body('[data-testid="notification-item-2"]')!.textContent).toContain('已核准： PO-0002');
    w.unmount();
  });

  it('approval notification link opens the record detail', async () => {
    const { w, push } = await mountBell({ items: [note(7, { kind: 'approvalPending', title: 'PO-0007', link: '/dynamic/m/5' })], unreadCount: 1 });
    await w.get('[data-testid="notification-bell"]').trigger('click');
    await flushPromises();
    body('[data-testid="notification-item-7"]')!.click();
    await flushPromises();
    expect(push).toHaveBeenCalledWith('/dynamic/m/5');
    w.unmount();
  });
});

describe('notification kinds', () => {
  const APPROVAL = [
    'approvalPending', 'approvalTransferred', 'approvalAddSigned', 'approvalRejected', 'approvalReturned', 'approvalApproved',
    'approvalWithdrawn', 'approvalCancelled', 'approvalRerouted', 'approvalOverdue', 'approvalAdminAlert',
  ];
  it('NOTIFICATION_KINDS lists all 11 approval kinds the server writes', () => {
    for (const k of APPROVAL) expect(NOTIFICATION_KINDS as readonly string[]).toContain(k);
  });
  it('every NOTIFICATION_KINDS entry has a label in all four locales, and the foreign ones hold no Chinese', () => {
    const locales = { 'zh-TW': zhTW, 'en-US': enUS, th, vi: vi_ } as Record<string, { notifications: { kind: Record<string, string> } }>;
    for (const [name, m] of Object.entries(locales)) {
      for (const k of NOTIFICATION_KINDS) {
        const text = m.notifications.kind[k];
        expect(text, `${name} ${k}`).toBeTruthy();
        if (name !== 'zh-TW') expect(text, `${name} ${k}`).not.toMatch(/[\u4e00-\u9fff]/);
      }
    }
  });
});
