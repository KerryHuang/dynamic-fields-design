import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import { assigneeBoardWire, boardWire, cardWire, columnWire, configWire, dueBoardWire, shareTargetsWire, viewDetailWire, viewSummaryWire } from 'src/presentation/tasks/board/board-fixtures';
import zhTW from 'src/i18n/zh-TW.json';
import { commentWire, taskDetailWire, taskWire, USERS } from 'src/presentation/dynamic/tasks/task-fixtures';
import { useIdentityStore } from 'src/stores/use-identity-store';
import { bodyOf, HttpFail, mockHttp, type Call } from 'src/test-utils/mock-http';
import DateSelect from 'src/components/framework/DateSelect.vue';
import MyTasksPage from './MyTasksPage.vue';

let calls: Call[];

async function mountPage(url: string, opts: { detailFails?: boolean; empty?: boolean; subordinates?: boolean; goneBoards?: number[]; defaultBoardFails?: boolean; admin?: boolean; noScope?: boolean; defaultConfig?: Record<string, unknown>; putConflict?: boolean } = {}) {
  let detailVersion = 'v1';
  let conflicted = false;
  const pinia = createPinia();
  setActivePinia(pinia);
  useIdentityStore().setSession('sales01');
  calls = mockHttp((c) => {
    if (c.url === '/tasks' && c.method === 'get') return opts.empty ? { items: [], totalCount: 0, currentPage: 1, pagesSize: 20 } : { items: [taskWire({ id: 1 }), taskWire({ id: 2, title: '寄合約' })], totalCount: 2, currentPage: 1, pagesSize: 20 };
    if (c.url === '/task-board/columns') {
      const viewId = (bodyOf(c) as { viewId: number | null }).viewId;
      if (viewId === null && opts.defaultBoardFails) return new HttpFail(500, { message: '伺服器錯誤' });
      if (viewId !== null && opts.goneBoards?.includes(viewId)) return new HttpFail(404, { message: '找不到看板' });
      // 欄查詢的回應帶著它採用的設定：未存的設定原樣回，看板 8 是依到期區間，預設看板用伺服器決定的預設
      const sent = (bodyOf(c) as { config: Record<string, unknown> | null }).config;
      const cfg = sent ?? (viewId === 8 ? configWire({ groupBy: 'dueBucket', scope: 'visible' }) : opts.defaultConfig ?? configWire());
      if (sent?.filters && (sent.filters as { keyword?: string }).keyword === 'BAD') return new HttpFail(400, { message: '設定無效' });
      const base = cfg.groupBy === 'dueBucket' ? dueBoardWire() : cfg.groupBy === 'assignee' ? assigneeBoardWire() : boardWire();
      if (viewId === 8) base.columns = base.columns.map((col) => (col.key === 'overdue' ? { ...col, total: 1, cards: [cardWire({ id: 31, title: '逾期的報價', overdue: true })] } : col));
      return { ...base, config: cfg, ...(opts.noScope ? { scopeAvailable: false } : {}) };
    }
    if (c.url === '/task-views' && c.method === 'get') return { mine: [viewSummaryWire({ id: 5, name: '我的到期' })], shared: [viewSummaryWire({ id: 8, name: '業務部任務', isOwner: false, ownerUserId: 'manager01', ownerName: '業務主管', shareCount: 1 })] };
    if (c.url === '/task-views' && c.method === 'post') return viewDetailWire({ id: 11, name: (bodyOf(c) as { name: string }).name, config: (bodyOf(c) as { config: unknown }).config });
    if (c.url === '/task-views/5' && c.method === 'get') return viewDetailWire({ rowVersion: detailVersion });
    if (c.url === '/task-views/5' && c.method === 'put') {
      // 第一次儲存撞 409，之後重抓看板就是別人改過的新版本（v2）
      if (opts.putConflict && !conflicted) { conflicted = true; detailVersion = 'v2'; return new HttpFail(409, { message: '伺服器訊息' }); }
      return viewDetailWire({ ...(bodyOf(c) as object), rowVersion: 'v3' });
    }
    if (c.url === '/task-views/8' && c.method === 'get') return viewDetailWire({ id: 8, name: '業務部任務', isOwner: false, canEdit: false, canDelete: false, config: configWire({ groupBy: 'dueBucket', scope: 'visible' }) });
    if (/^\/task-views\/\d+$/.test(c.url) && c.method === 'delete') return {};
    if (c.url === '/task-views/share-targets') return shareTargetsWire();
    if (c.url === '/me') return { userId: 'sales01', isSystemAdmin: !!opts.admin };
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
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], pinia, router, i18n, [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false, refetchOnWindowFocus: false } } }) }]] },
  });
  await flushPromises();
  await new Promise((r) => setTimeout(r, 20));
  await flushPromises();
  return { w, router };
}
/** 選擇器按鈕的文字（去掉下拉箭頭圖示的字）。 */
const pickerText = (w: { get: (s: string) => { text: () => string } }) => w.get('[data-testid="board-picker"]').text().replace('arrow_drop_down', '');
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

  it('新增對話框有開始日：晚於到期日時欄位下顯示錯誤、不送出，改好後一併送出（G12）', async () => {
    const { w } = await mountPage('/tasks');
    await w.get('[data-testid="my-task-add"]').trigger('click');
    await flushPromises();
    const title = document.body.querySelector<HTMLInputElement>('input[data-testid="task-form-title"]')!;
    title.value = '佈置展場';
    title.dispatchEvent(new Event('input'));
    const dates = w.findAllComponents(DateSelect);
    const start = dates.find((d) => d.props('label') === '開始日')!;
    const due = dates.find((d) => d.props('label') === '到期日')!;
    start.vm.$emit('update:modelValue', '2026-11-26');
    due.vm.$emit('update:modelValue', '2026-11-25');
    await flushPromises();
    expect(document.body.querySelector('[data-testid="task-form-start"]')!.closest('.q-field')!.textContent).toContain('開始日不能晚於到期日');
    document.body.querySelector<HTMLElement>('[data-testid="task-form-submit"]')!.click();
    await flushPromises();
    expect(calls.find((c) => c.url === '/tasks' && c.method === 'post')).toBeUndefined();
    start.vm.$emit('update:modelValue', '2026-11-20');
    await flushPromises();
    document.body.querySelector<HTMLElement>('[data-testid="task-form-submit"]')!.click();
    await flushPromises();
    expect(bodyOf(calls.find((c) => c.url === '/tasks' && c.method === 'post'))).toMatchObject({ startDate: '2026-11-20', dueDate: '2026-11-25' });
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
    expect(pickerText(w)).toBe('預設看板');
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
    expect(pickerText(w)).toBe('我的到期');
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

  it('a board id that is not a number also falls back with the notice, but keeps the remembered board (the link was bad, not the memory)', async () => {
    localStorage.setItem('taskBoard.last.sales01', '5');
    const { w, router } = await mountPage('/tasks?view=board&board=abc');
    expect(localStorage.getItem('taskBoard.last.sales01')).toBe('5');
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

  it('when the default board also fails after falling back, the notice and the retry button show together', async () => {
    const { w } = await mountPage('/tasks?view=board&board=7', { goneBoards: [7], defaultBoardFails: true });
    expect(w.find('[data-testid="board-gone-notice"]').exists()).toBe(true);
    expect(w.find('[data-testid="board-load-failed"]').exists()).toBe(true);
    expect(w.find('[data-testid="board-retry"]').exists()).toBe(true);
    w.unmount();
  });

  it('the view switch is a labelled group', async () => {
    const { w } = await mountPage('/tasks');
    const toggle = w.get('[data-testid="view-toggle"]');
    expect(toggle.attributes('role')).toBe('group');
    expect(toggle.attributes('aria-label')).toBe('檢視方式');
    w.unmount();
  });

  it('saving a task from the dialog on the board reloads the board columns', async () => {
    const { w } = await mountPage('/tasks?view=board&open=7');
    const boardCalls = () => calls.filter((c) => c.url === '/task-board/columns').length;
    const before = boardCalls();
    const title = document.body.querySelector<HTMLInputElement>('input[data-testid="task-title"]')!;
    title.value = '改過的標題';
    title.dispatchEvent(new Event('input'));
    await flushPromises();
    document.body.querySelector<HTMLElement>('[data-testid="task-save"]')!.click();
    await flushPromises();
    await new Promise((r) => setTimeout(r, 20));
    await flushPromises();
    expect(calls.some((c) => /^\/tasks\/7$/.test(c.url) && c.method !== 'get')).toBe(true);
    expect(boardCalls()).toBe(before + 1);
    w.unmount();
  });

  it('?open= on the board opens the same task dialog', async () => {
    const { w } = await mountPage('/tasks?view=board&open=7');
    expect(calls.some((c) => c.url === '/tasks/7')).toBe(true);
    expect(document.body.querySelector('[data-testid="task-dialog"]')).not.toBeNull();
    w.unmount();
  });

  describe('board picker, settings and sharing', () => {
    const lastBoardBody = () => bodyOf(calls.filter((c) => c.url === '/task-board/columns').at(-1)) as { viewId: number | null; config: Record<string, unknown> | null };
    const body = (id: string) => document.body.querySelector<HTMLElement>(`[data-testid="${id}"]`);
    const click = async (id: string) => { body(id)!.click(); await flushPromises(); await new Promise((r) => setTimeout(r, 20)); await flushPromises(); };

    it('MyTasksPage picking a board puts it in the url and loads it', async () => {
      const { w, router } = await mountPage('/tasks?view=board');
      await click('board-picker');
      await click('picker-item-8');
      expect(router.currentRoute.value.query.board).toBe('8');
      expect(lastBoardBody()).toEqual({ viewId: 8, config: null });
      expect(pickerText(w)).toBe('業務部任務');
      w.unmount();
    });

    it('MyTasksPage choosing the default board clears the board param and the remembered board', async () => {
      const { w, router } = await mountPage('/tasks?view=board&board=5');
      expect(localStorage.getItem('taskBoard.last.sales01')).toBe('5');
      await click('board-picker');
      await click('picker-item-default');
      expect(router.currentRoute.value.query.board).toBeUndefined();
      expect(localStorage.getItem('taskBoard.last.sales01')).toBeNull();
      expect(lastBoardBody()).toEqual({ viewId: null, config: null });
      w.unmount();
    });

    it('MyTasksPage apply loads the board by the unsaved config and marks it unsaved, saving nothing', async () => {
      const { w, router } = await mountPage('/tasks?view=board&board=5');
      await click('board-settings');
      await click('settings-group-dueBucket');
      await click('settings-apply');
      expect(lastBoardBody().viewId).toBeNull();
      expect(lastBoardBody().config).toMatchObject({ groupBy: 'dueBucket', scope: 'assigned' });
      expect(pickerText(w)).toBe('我的到期（未儲存）');
      expect(calls.some((c) => c.method === 'put')).toBe(false);
      // 網址仍帶原看板：重新整理會回到存好的版本
      expect(router.currentRoute.value.query.board).toBe('5');
      w.unmount();
    });

    it('picking another board drops the unsaved config', async () => {
      const { w } = await mountPage('/tasks?view=board&board=5');
      await click('board-settings');
      await click('settings-apply');
      expect(pickerText(w)).toContain('（未儲存）');
      await click('board-picker');
      await click('picker-item-default');
      expect(pickerText(w)).toBe('預設看板');
      expect(lastBoardBody()).toEqual({ viewId: null, config: null });
      w.unmount();
    });

    it('saving from the settings dialog writes back with rowVersion and clears the unsaved mark', async () => {
      const { w } = await mountPage('/tasks?view=board&board=5');
      await click('board-settings');
      await click('settings-scope-visible');
      await click('settings-save');
      const put = calls.find((c) => c.method === 'put' && c.url === '/task-views/5')!;
      expect(bodyOf(put)).toMatchObject({ rowVersion: 'v1', config: { scope: 'visible' } });
      expect(pickerText(w)).toBe('我的到期');
      w.unmount();
    });

    it('the settings dialog offers save only on your own saved board', async () => {
      const own = await mountPage('/tasks?view=board&board=5');
      await click('board-settings');
      expect(body('settings-save')).not.toBeNull();
      own.w.unmount();
      document.body.innerHTML = '';
      const shared = await mountPage('/tasks?view=board&board=8');
      await click('board-settings');
      expect(body('settings-apply')).not.toBeNull();
      expect(body('settings-save')).toBeNull();
      shared.w.unmount();
      document.body.innerHTML = '';
      const def = await mountPage('/tasks?view=board');
      await click('board-settings');
      expect(body('settings-save')).toBeNull();
      expect(body('settings-save-as')).not.toBeNull();
      def.w.unmount();
    });

    it('a board saved as new from the picker becomes the current board', async () => {
      const { w, router } = await mountPage('/tasks?view=board');
      await click('board-picker');
      await click('picker-save-as');
      const input = body('picker-name') as HTMLInputElement;
      input.value = '我的新看板';
      input.dispatchEvent(new Event('input'));
      await flushPromises();
      await click('picker-name-ok');
      expect(router.currentRoute.value.query.board).toBe('11');
      expect(bodyOf(calls.find((c) => c.method === 'post' && c.url === '/task-views'))).toMatchObject({ kind: 'board', name: '我的新看板' });
      w.unmount();
    });

    it('deleting the board you are on goes back to the default board and forgets it', async () => {
      const { w, router } = await mountPage('/tasks?view=board&board=5');
      await click('board-picker');
      await click('picker-delete-5');
      await click('picker-delete-ok');
      expect(calls.some((c) => c.method === 'delete' && c.url === '/task-views/5')).toBe(true);
      expect(router.currentRoute.value.query.board).toBeUndefined();
      expect(localStorage.getItem('taskBoard.last.sales01')).toBeNull();
      expect(w.find('[data-testid="board-gone-notice"]').exists()).toBe(false);
      w.unmount();
    });

    it('the share button exists only on your own saved board and opens the share dialog', async () => {
      const shared = await mountPage('/tasks?view=board&board=8');
      expect(shared.w.find('[data-testid="board-share"]').exists()).toBe(false);
      shared.w.unmount();
      document.body.innerHTML = '';
      const { w } = await mountPage('/tasks?view=board&board=5');
      await click('board-share');
      expect(body('task-share')).not.toBeNull();
      w.unmount();
    });

    it('MyTasksPage shows the no-subordinates notice when the scope is unavailable (D7), and keeps the board open', async () => {
      const { w } = await mountPage('/tasks?view=board&board=5', { noScope: true });
      expect(w.get('[data-testid="scope-unavailable-notice"]').text()).toContain('這個看板的範圍是「部屬的任務」，你目前沒有部屬');
      expect(w.find('[data-testid="task-board"]').exists()).toBe(true);
      w.unmount();
      const ok = await mountPage('/tasks?view=board&board=5');
      expect(ok.w.find('[data-testid="scope-unavailable-notice"]').exists()).toBe(false);
      ok.w.unmount();
    });

    it('moving a card on a board uses the group-by of the config that came with the columns', async () => {
      const { w } = await mountPage('/tasks?view=board&board=8');
      // 看板 8 依到期區間；欄與設定來自同一份回應，移動請求帶的分欄依據一定和欄一致
      await w.get('[data-testid="board-card-move-31"]').trigger('click');
      await flushPromises();
      document.body.querySelector<HTMLElement>('[data-testid="move-item-none"]')!.click();
      await flushPromises();
      const move = calls.find((c) => c.url === '/tasks/31/move')!;
      expect(bodyOf(move)).toMatchObject({ groupBy: 'dueBucket', toColumn: 'none' });
      w.unmount();
    });

    it('the default board settings start from the config the server answered, not from a copy in the page', async () => {
      const { w } = await mountPage('/tasks?view=board', { defaultConfig: configWire({ sort: { key: 'title', dir: 'desc' }, scope: 'involved' }) });
      await click('board-settings');
      // 預設看板另存時名稱不預填「預設看板」
      expect((body('settings-name') as HTMLInputElement).value).toBe('');
      const input = body('settings-name') as HTMLInputElement;
      input.value = '我的新看板';
      input.dispatchEvent(new Event('input'));
      await flushPromises();
      await click('settings-save-as');
      expect(bodyOf(calls.find((c) => c.method === 'post' && c.url === '/task-views'))).toMatchObject({
        name: '我的新看板', config: { scope: 'involved', sort: { key: 'title', dir: 'desc' } },
      });
      w.unmount();
    });

    it('a 409 on save refetches the board, so reopening the settings sends the new rowVersion', async () => {
      const { w } = await mountPage('/tasks?view=board&board=5', { putConflict: true });
      await click('board-settings');
      await click('settings-save');
      expect(body('settings-error')!.textContent).toContain(zhTW.taskBoard.settings.stale);
      await click('settings-cancel');
      await new Promise((r) => setTimeout(r, 400));
      await click('board-settings');
      await click('settings-save');
      const puts = calls.filter((c) => c.method === 'put' && c.url === '/task-views/5');
      expect(puts.map((c) => (bodyOf(c) as { rowVersion: string }).rowVersion)).toEqual(['v1', 'v2']);
      w.unmount();
    });

    it('an unsaved config the server rejects (400) says so and offers to discard it, instead of retrying forever', async () => {
      const { w } = await mountPage('/tasks?view=board&board=5');
      await click('board-settings');
      const kw = body('settings-keyword') as HTMLInputElement;
      kw.value = 'BAD';
      kw.dispatchEvent(new Event('input'));
      await flushPromises();
      await click('settings-apply');
      await new Promise((r) => setTimeout(r, 50));
      await flushPromises();
      expect(w.find('[data-testid="board-invalid"]').text()).toContain(zhTW.taskBoard.invalidConfig);
      expect(w.find('[data-testid="board-load-failed"]').exists()).toBe(false);
      await w.get('[data-testid="board-discard-draft"]').trigger('click');
      await flushPromises();
      expect(w.find('[data-testid="board-invalid"]').exists()).toBe(false);
      expect(pickerText(w)).toBe('我的到期');
      expect(w.find('[data-testid="task-board"]').exists()).toBe(true);
      w.unmount();
    });

    it('the unsaved config is dropped whenever the board in the url changes, even by a path that does not clear it itself', async () => {
      const { w, router } = await mountPage('/tasks?view=board&board=5');
      await click('board-settings');
      await click('settings-apply');
      expect(pickerText(w)).toContain('（未儲存）');
      await router.push('/tasks?view=board&board=8');
      await flushPromises();
      expect(pickerText(w)).toBe('業務部任務');
    });
  });
});
