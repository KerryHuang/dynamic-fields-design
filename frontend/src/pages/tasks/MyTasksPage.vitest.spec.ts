import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import { boardWire } from 'src/presentation/tasks/board/board-fixtures';
import zhTW from 'src/i18n/zh-TW.json';
import { commentWire, taskDetailWire, taskWire, USERS } from 'src/presentation/dynamic/tasks/task-fixtures';
import { useIdentityStore } from 'src/stores/use-identity-store';
import { bodyOf, HttpFail, mockHttp, type Call } from 'src/test-utils/mock-http';
import MyTasksPage from './MyTasksPage.vue';

let calls: Call[];

async function mountPage(url: string, opts: { detailFails?: boolean; empty?: boolean; subordinates?: boolean; goneBoards?: number[] } = {}) {
  const pinia = createPinia();
  setActivePinia(pinia);
  useIdentityStore().setSession('sales01');
  calls = mockHttp((c) => {
    if (c.url === '/tasks' && c.method === 'get') return opts.empty ? { items: [], totalCount: 0, currentPage: 1, pagesSize: 20 } : { items: [taskWire({ id: 1 }), taskWire({ id: 2, title: '寄合約' })], totalCount: 2, currentPage: 1, pagesSize: 20 };
    if (c.url === '/task-board/columns') {
      const viewId = (bodyOf(c) as { viewId: number | null }).viewId;
      return viewId !== null && opts.goneBoards?.includes(viewId) ? new HttpFail(404, { message: '找不到看板' }) : boardWire();
    }
    if (c.url === '/task-views') return { mine: [{ id: 5, name: '我的到期', isOwner: true }], shared: [] };
    if (c.url === '/tasks/capabilities') return { subordinates: !!opts.subordinates };
    if (c.url === '/tasks' && c.method === 'post') return taskDetailWire({ id: 99 });
    if (/^\/tasks\/\d+$/.test(c.url)) return opts.detailFails ? new HttpFail(404, { message: '找不到任務' }) : taskDetailWire({ id: 7 });
    if (/comments$/.test(c.url)) return [commentWire()];
    if (c.url === '/users') return USERS;
    if (c.url === '/attachments/settings') return { uploadEnabled: true, maxBytes: 1, allowedExtensions: [], maxFilesPerField: 1 };
    return [];
  });
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/tasks', component: MyTasksPage }, { path: '/:p(.*)*', component: { render: () => null } }] });
  await router.push(url);
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  // q-page needs a surrounding layout
  const Host = { render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(MyTasksPage))) };
  const w = mount(Host, {
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], pinia, router, i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
  });
  await flushPromises();
  await new Promise((r) => setTimeout(r, 20));
  await flushPromises();
  return { w, router };
}
const listCalls = () => calls.filter((c) => c.url === '/tasks' && c.method === 'get');

describe('MyTasksPage', () => {
  beforeEach(() => {
    document.body.innerHTML = '';
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });
  afterEach(() => { document.body.innerHTML = ''; localStorage.clear(); });

  it('預設請求 GET /tasks?tab=assigned&filter=open&currentPage=1&pagesSize=20', async () => {
    const { w } = await mountPage('/tasks');
    expect(listCalls()[0].params).toEqual({ tab: 'assigned', filter: 'open', currentPage: 1, pagesSize: 20 });
    expect(w.findAll('[data-testid^="my-task-"]').filter((e) => /^my-task-\d+$/.test(e.attributes('data-testid')!))).toHaveLength(2);
    w.unmount();
  });

  it('MyTasksPage shows the subordinates tab only when capabilities say so', async () => {
    const without = await mountPage('/tasks');
    expect(without.w.find('[data-testid="tab-subordinates"]').exists()).toBe(false);
    expect(without.w.findAll('[data-testid^="tab-"]')).toHaveLength(3);
    without.w.unmount();
    const withTab = await mountPage('/tasks', { subordinates: true });
    expect(withTab.w.find('[data-testid="tab-subordinates"]').exists()).toBe(true);
    expect(withTab.w.get('[data-testid="tab-subordinates"]').text()).toContain('部屬的任務');
    await withTab.w.get('[data-testid="tab-subordinates"]').trigger('click');
    await flushPromises();
    expect(listCalls().at(-1)!.params).toMatchObject({ tab: 'subordinates', filter: 'open', currentPage: 1 });
    withTab.w.unmount();
  });

  it('a link to the subordinates tab falls back to assigned when the user has no subordinates', async () => {
    const { w, router } = await mountPage('/tasks?tab=subordinates');
    expect(listCalls().at(-1)!.params).toMatchObject({ tab: 'assigned' });
    expect(w.find('[data-testid="tab-subordinates"]').exists()).toBe(false);
    expect(router.currentRoute.value.query.tab).toBe('subordinates');
    w.unmount();
  });

  it('subordinates tab rows show the assignee name', async () => {
    const { w } = await mountPage('/tasks?tab=subordinates', { subordinates: true });
    expect(w.get('[data-testid="my-task-assignee-1"]').text()).toBe('業務一');
    w.unmount();
  });

  it('切到「提到我的」改送 tab=mentioned，網址帶 tab', async () => {
    const { w, router } = await mountPage('/tasks');
    await w.get('[data-testid="tab-mentioned"]').trigger('click');
    await flushPromises();
    expect(listCalls().at(-1)!.params).toMatchObject({ tab: 'mentioned', filter: 'open', currentPage: 1 });
    expect(router.currentRoute.value.query.tab).toBe('mentioned');
    w.unmount();
  });

  it('網址帶 filter 時照該篩選請求', async () => {
    const { w } = await mountPage('/tasks?tab=created&filter=overdue');
    expect(listCalls()[0].params).toMatchObject({ tab: 'created', filter: 'overdue' });
    w.unmount();
  });

  it('/tasks?open=7 會打開對話框並請求 /tasks/7；關掉時移除 open', async () => {
    const { w, router } = await mountPage('/tasks?open=7');
    expect(calls.some((c) => c.url === '/tasks/7')).toBe(true);
    expect(document.body.querySelector('[data-testid="task-dialog"]')).not.toBeNull();
    w.findComponent({ name: 'TaskDialog' }).vm.$emit('update:taskId', null);
    await flushPromises();
    expect(router.currentRoute.value.query.open).toBeUndefined();
    w.unmount();
  });

  it('?open= 指到不存在或看不到的任務（404）時顯示提示，不卡住', async () => {
    const { w } = await mountPage('/tasks?open=7', { detailFails: true });
    expect(document.body.querySelector('[data-testid="task-not-found"]')!.textContent).toContain('找不到這個任務');
    w.unmount();
  });

  it('404 的對話框可按關閉鈕，網址的 open 被移除', async () => {
    const { w, router } = await mountPage('/tasks?open=7', { detailFails: true });
    document.body.querySelector<HTMLElement>('[data-testid="task-dialog-close"]')!.click();
    await flushPromises();
    await new Promise((r) => setTimeout(r, 30));
    await flushPromises();
    expect(router.currentRoute.value.query.open).toBeUndefined();
    w.unmount();
  });

  it('在對話框點相關記錄會導到記錄頁，不會被關閉對話框的導航蓋掉', async () => {
    const { w, router } = await mountPage('/tasks?open=7');
    document.body.querySelector<HTMLElement>('[data-testid="task-record"] a')!.click();
    await flushPromises();
    await new Promise((r) => setTimeout(r, 30));
    expect(router.currentRoute.value.path).toBe('/dynamic/crm_customer/9');
    w.unmount();
  });

  it('?open= 不是數字時忽略，不打開對話框', async () => {
    const { w } = await mountPage('/tasks?open=abc');
    expect(document.body.querySelector('[data-testid="task-dialog"]')).toBeNull();
    w.unmount();
  });

  it('點列表的任務會把 open 寫進網址', async () => {
    const { w, router } = await mountPage('/tasks');
    await w.get('[data-testid="my-task-open-2"]').trigger('click');
    await flushPromises();
    expect(router.currentRoute.value.query.open).toBe('2');
    w.unmount();
  });

  it('新增個人任務時負責人預設是目前使用者', async () => {
    const { w } = await mountPage('/tasks');
    await w.get('[data-testid="my-task-add"]').trigger('click');
    await flushPromises();
    const title = document.body.querySelector<HTMLInputElement>('input[data-testid="task-form-title"]')!;
    title.value = '整理客戶清單';
    title.dispatchEvent(new Event('input'));
    await flushPromises();
    document.body.querySelector<HTMLElement>('[data-testid="task-form-submit"]')!.click();
    await flushPromises();
    const post = calls.find((c) => c.url === '/tasks' && c.method === 'post');
    expect(bodyOf(post)).toMatchObject({ title: '整理客戶清單', assigneeUserId: 'sales01' });
    w.unmount();
  });

  it('沒有任務時顯示空白說明', async () => {
    const { w } = await mountPage('/tasks', { empty: true });
    expect(w.text()).toContain('目前沒有任務');
    w.unmount();
  });

  it('the page and the menu are called Tasks, not My tasks', async () => {
    const { w } = await mountPage('/tasks');
    expect(w.find('.text-h6').text()).toBe('任務');
    w.unmount();
  });

  it('MyTasksPage keeps view and board in the url', async () => {
    const { w, router } = await mountPage('/tasks');
    expect(w.find('[data-testid="task-board"]').exists()).toBe(false);
    const listCallsBefore = listCalls().length;
    await w.get('[data-testid="view-board"]').trigger('click');
    await flushPromises();
    expect(router.currentRoute.value.query).toMatchObject({ view: 'board' });
    expect(router.currentRoute.value.query.board).toBeUndefined();
    const boardCalls = () => calls.filter((c) => c.url === '/task-board/columns');
    expect(bodyOf(boardCalls().at(-1))).toEqual({ viewId: null, config: null });
    expect(w.find('[data-testid="task-board"]').exists()).toBe(true);
    expect(w.find('[data-testid="tab-assigned"]').exists()).toBe(false);
    expect(w.get('[data-testid="board-picker-slot"]').text()).toBe('預設看板');
    expect(listCalls()).toHaveLength(listCallsBefore);
    await w.get('[data-testid="view-list"]').trigger('click');
    await flushPromises();
    expect(router.currentRoute.value.query.view).toBeUndefined();
    expect(w.find('[data-testid="task-board"]').exists()).toBe(false);
    expect(w.find('[data-testid="tab-assigned"]').exists()).toBe(true);
    w.unmount();
  });

  it('a board id in the url loads that board, shows its name and is remembered; switching to the board again reopens it', async () => {
    const { w, router } = await mountPage('/tasks?view=board&board=5');
    expect(bodyOf(calls.filter((c) => c.url === '/task-board/columns').at(-1))).toEqual({ viewId: 5, config: null });
    expect(w.get('[data-testid="board-picker-slot"]').text()).toBe('我的到期');
    expect(localStorage.getItem('taskBoard.last.sales01')).toBe('5');
    await w.get('[data-testid="view-list"]').trigger('click');
    await flushPromises();
    await w.get('[data-testid="view-board"]').trigger('click');
    await flushPromises();
    expect(router.currentRoute.value.query).toMatchObject({ view: 'board', board: '5' });
    w.unmount();
  });

  it('falls back to the default board with a notice when the board is gone', async () => {
    localStorage.setItem('taskBoard.last.sales01', '7');
    const { w, router } = await mountPage('/tasks?view=board&board=7', { goneBoards: [7] });
    expect(router.currentRoute.value.query.board).toBeUndefined();
    expect(router.currentRoute.value.query.view).toBe('board');
    expect(w.get('[data-testid="board-gone-notice"]').text()).toContain('這個看板已刪除或不再分享給你');
    expect(localStorage.getItem('taskBoard.last.sales01')).toBeNull();
    expect(bodyOf(calls.filter((c) => c.url === '/task-board/columns').at(-1))).toEqual({ viewId: null, config: null });
    expect(w.find('[data-testid="task-board"]').exists()).toBe(true);
    expect(w.find('[data-testid="board-load-failed"]').exists()).toBe(false);
    await w.get('[data-testid="board-gone-close"]').trigger('click');
    expect(w.find('[data-testid="board-gone-notice"]').exists()).toBe(false);
    w.unmount();
  });

  it('a board id that is not a number also falls back with the notice', async () => {
    const { w, router } = await mountPage('/tasks?view=board&board=abc');
    expect(router.currentRoute.value.query.board).toBeUndefined();
    expect(w.find('[data-testid="board-gone-notice"]').exists()).toBe(true);
    expect(calls.filter((c) => c.url === '/task-board/columns').every((c) => (bodyOf(c) as { viewId: number | null }).viewId === null)).toBe(true);
    w.unmount();
  });

  it('a remembered board that is gone is dropped when switching to the board view', async () => {
    localStorage.setItem('taskBoard.last.sales01', '7');
    const { w, router } = await mountPage('/tasks', { goneBoards: [7] });
    await w.get('[data-testid="view-board"]').trigger('click');
    await flushPromises();
    await flushPromises();
    expect(router.currentRoute.value.query.board).toBeUndefined();
    expect(w.find('[data-testid="board-gone-notice"]').exists()).toBe(true);
    expect(localStorage.getItem('taskBoard.last.sales01')).toBeNull();
    w.unmount();
  });

  it('?open= on the board opens the same task dialog', async () => {
    const { w } = await mountPage('/tasks?view=board&open=7');
    expect(calls.some((c) => c.url === '/tasks/7')).toBe(true);
    expect(document.body.querySelector('[data-testid="task-dialog"]')).not.toBeNull();
    w.unmount();
  });
});
