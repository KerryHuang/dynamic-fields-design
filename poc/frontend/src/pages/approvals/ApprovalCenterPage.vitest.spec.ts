import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import type { ApprovalInboxItem } from 'src/domain/dynamic/approval-types';
import zhTW from 'src/i18n/zh-TW.json';
import { useIdentityStore } from 'src/stores/use-identity-store';
import { HttpFail, mockHttp, type Call } from 'src/test-utils/mock-http';
import ApprovalCenterPage from './ApprovalCenterPage.vue';

let calls: Call[];

const item = (o: Partial<ApprovalInboxItem> = {}): ApprovalInboxItem => ({
  taskId: 11, instanceId: 5, recordId: 9, moduleKey: 'po', moduleName: { 'zh-TW': '採購單' }, titleSnapshot: 'PO-0009 辦公椅',
  stageName: { 'zh-TW': '財務審核' }, initiatorUserId: 'sales01', initiatorName: '業務一', submittedTime: '2026-11-10T09:30:00',
  dueTime: null, overdue: false, moduleClosed: false, status: 'pending', ...o,
});

interface Opts { items?: ApprovalInboxItem[]; total?: number; allForbidden?: boolean; listFails?: boolean; width?: number }

async function mountPage(url: string, opts: Opts = {}) {
  window.innerWidth = opts.width ?? 1366;
  const pinia = createPinia();
  setActivePinia(pinia);
  useIdentityStore().setSession('admin');
  const items = opts.items ?? [item(), item({ taskId: 12, instanceId: 6, recordId: 10, titleSnapshot: 'PO-0010 螢幕' })];
  calls = mockHttp((c) => {
    if (c.url === '/approval-tasks') {
      if (c.params?.box === 'all' && c.params?.pagesSize === 1) return opts.allForbidden ? new HttpFail(403) : { items: [], totalCount: 0, currentPage: 1, pagesSize: 1 };
      if (opts.listFails) return new HttpFail(500, {});
      return { items, totalCount: opts.total ?? items.length, currentPage: Number(c.params?.currentPage ?? 1), pagesSize: 20 };
    }
    if (c.url === '/menu') return [{ key: 'po', name: { 'zh-TW': '採購單' } }, { key: 'exp', name: { 'zh-TW': '費用報支' } }];
    return [];
  });
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/approvals', component: ApprovalCenterPage }, { path: '/:p(.*)*', component: { render: () => null } }] });
  await router.push(url);
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const Host = { render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(ApprovalCenterPage))) };
  const w = mount(Host, {
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], pinia, router, i18n, [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]] },
  });
  // Quasar's Screen follows window width through a debounced resize listener
  window.dispatchEvent(new Event('resize'));
  await new Promise((r) => setTimeout(r, 150));
  await flushPromises();
  return { w, router };
}
const lists = () => calls.filter((c) => c.url === '/approval-tasks' && !(c.params?.box === 'all' && c.params?.pagesSize === 1));
const q = (id: string) => document.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

describe('ApprovalCenterPage', () => {
  beforeEach(() => {
    document.body.innerHTML = '';
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });
  afterEach(() => { document.body.innerHTML = ''; });

  it('defaults to the todo tab and lists title, module, stage, initiator and submitted time', async () => {
    const { w } = await mountPage('/approvals');
    expect(lists()[0].params).toMatchObject({ box: 'todo', currentPage: 1, pagesSize: 20 });
    const row = q('approval-row-5')!;
    expect(row.textContent).toContain('PO-0009 辦公椅');
    expect(row.textContent).toContain('採購單');
    expect(row.textContent).toContain('財務審核');
    expect(row.textContent).toContain('業務一');
    expect(row.textContent).toContain('2026-11-10 09:30');
    expect(q('approval-row-6')).not.toBeNull();
    w.unmount();
  });

  it('a row that is no longer pending shows its status instead of a stage', async () => {
    const { w } = await mountPage('/approvals?tab=submitted', { items: [item({ status: 'approved', stageName: null })] });
    expect(q('approval-row-5')!.textContent).toContain('已核准');
    w.unmount();
  });

  it('overdue row shows the overdue badge', async () => {
    const { w } = await mountPage('/approvals', { items: [item({ overdue: true, dueTime: '2026-11-09T18:00:00' }), item({ instanceId: 6, taskId: 12 })] });
    expect(q('approval-overdue-5')!.textContent).toContain('逾期');
    expect(q('approval-overdue-6')).toBeNull();
    w.unmount();
  });

  it('closed module row shows the closed badge', async () => {
    const { w } = await mountPage('/approvals', { items: [item({ moduleClosed: true })] });
    expect(q('approval-closed-5')!.textContent).toContain('應用程式已關閉');
    w.unmount();
  });

  it('row click and Enter and Space navigate to the record detail', async () => {
    const { w, router } = await mountPage('/approvals');
    expect(q('approval-row-5')!.getAttribute('tabindex')).toBe('0');
    q('approval-row-5')!.click();
    await flushPromises();
    expect(router.currentRoute.value.fullPath).toBe('/dynamic/po/9');
    await router.push('/approvals');
    await flushPromises();
    q('approval-row-6')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    await flushPromises();
    expect(router.currentRoute.value.fullPath).toBe('/dynamic/po/10');
    await router.push('/approvals');
    await flushPromises();
    q('approval-row-5')!.dispatchEvent(new KeyboardEvent('keydown', { key: ' ', bubbles: true }));
    await flushPromises();
    expect(router.currentRoute.value.fullPath).toBe('/dynamic/po/9');
    w.unmount();
  });

  it('all tab hidden when the probe returns 403', async () => {
    const { w } = await mountPage('/approvals', { allForbidden: true });
    expect(calls.some((c) => c.url === '/approval-tasks' && c.params?.box === 'all' && c.params?.pagesSize === 1)).toBe(true);
    expect(q('approval-tab-all')).toBeNull();
    expect(q('approval-tab-todo')).not.toBeNull();
    w.unmount();
  });

  it('a URL asking for the all tab falls back to todo when the probe returns 403', async () => {
    const { w } = await mountPage('/approvals?tab=all', { allForbidden: true });
    expect(lists().at(-1)!.params).toMatchObject({ box: 'todo' });
    w.unmount();
  });

  it('all tab shown when the probe succeeds', async () => {
    const { w } = await mountPage('/approvals');
    expect(q('approval-tab-all')).not.toBeNull();
    w.unmount();
  });

  it('status filter is sent and kept in the URL; todo tab has no status filter', async () => {
    const { w, router } = await mountPage('/approvals?tab=done');
    expect(q('approval-filter-status')).not.toBeNull();
    await router.push('/approvals?tab=done&status=approved&module=po&from=2026-11-01&to=2026-11-30');
    await flushPromises();
    await new Promise((r) => setTimeout(r, 20));
    expect(lists().at(-1)!.params).toMatchObject({ box: 'done', status: 'approved', module: 'po', from: '2026-11-01', to: '2026-11-30' });
    // todo: the URL still carries status, but it is neither shown nor sent
    await router.push('/approvals?tab=todo&status=approved');
    await flushPromises();
    await new Promise((r) => setTimeout(r, 20));
    expect(q('approval-filter-status')).toBeNull();
    expect(lists().at(-1)!.params).toMatchObject({ box: 'todo' });
    expect(lists().at(-1)!.params?.status).toBeUndefined();
    w.unmount();
  });

  it('picking a status writes it to the URL and refetches from page 1', async () => {
    const { w, router } = await mountPage('/approvals?tab=submitted');
    const filters = w.findComponent({ name: 'ApprovalInboxFilters' });
    filters.vm.$emit('update:modelValue', { status: 'rejected' });
    await flushPromises();
    await new Promise((r) => setTimeout(r, 20));
    expect(router.currentRoute.value.query).toMatchObject({ tab: 'submitted', status: 'rejected' });
    expect(lists().at(-1)!.params).toMatchObject({ box: 'submitted', status: 'rejected', currentPage: 1 });
    w.unmount();
  });

  it('paginates by 20 and asks for the chosen page', async () => {
    const { w } = await mountPage('/approvals', { total: 45 });
    expect(q('approval-pagination')).not.toBeNull();
    expect(document.querySelectorAll('[data-testid="approval-pagination"] button').length).toBeGreaterThan(2);
    const next = [...document.querySelectorAll('[data-testid="approval-pagination"] button')].find((b) => b.textContent?.trim() === '2') as HTMLElement;
    next.click();
    await flushPromises();
    await new Promise((r) => setTimeout(r, 20));
    expect(lists().at(-1)!.params).toMatchObject({ currentPage: 2, pagesSize: 20 });
    w.unmount();
  });

  it.each([['todo', '目前沒有待簽核的單據'], ['done', '目前沒有處理過的簽核'], ['submitted', '你還沒有送出過簽核']])('empty %s tab says so', async (tab, text) => {
    const { w } = await mountPage(`/approvals?tab=${tab}`, { items: [] });
    expect(q('approval-empty')!.textContent).toContain(text);
    w.unmount();
  });

  it('a failed load shows an error, not an empty state', async () => {
    const { w } = await mountPage('/approvals', { listFails: true });
    expect(q('approval-load-failed')).not.toBeNull();
    expect(q('approval-empty')).toBeNull();
    w.unmount();
  });

  it('mobile width renders focusable cards and a filter button', async () => {
    const { w } = await mountPage('/approvals?tab=done', { width: 390 });
    const card = q('approval-card-5')!;
    expect(card.getAttribute('tabindex')).toBe('0');
    expect(card.textContent).toContain('PO-0009 辦公椅');
    expect(q('approval-row-5')).toBeNull();
    expect(q('approval-filter-open')).not.toBeNull();
    expect(q('approval-filter-status')).toBeNull();
    w.unmount();
  });

  it('mobile filter dialog applies the draft only when 套用 is pressed', async () => {
    const { w, router } = await mountPage('/approvals?tab=done', { width: 390 });
    q('approval-filter-open')!.click();
    await flushPromises();
    await new Promise((r) => setTimeout(r, 50));
    const from = document.querySelector('[data-testid="approval-filter-from"]') as HTMLInputElement;
    from.value = '2026-11-01';
    from.dispatchEvent(new Event('input', { bubbles: true }));
    await flushPromises();
    expect(router.currentRoute.value.query.from).toBeUndefined();
    q('approval-filter-apply')!.click();
    await flushPromises();
    await new Promise((r) => setTimeout(r, 20));
    expect(router.currentRoute.value.query.from).toBe('2026-11-01');
    expect(lists().at(-1)!.params).toMatchObject({ box: 'done', from: '2026-11-01' });
    w.unmount();
  });

  it('desktop width keeps the table and has no filter button', async () => {
    const { w } = await mountPage('/approvals', { width: 1366 });
    expect(q('approval-card-5')).toBeNull();
    expect(q('approval-filter-open')).toBeNull();
    w.unmount();
  });
});
