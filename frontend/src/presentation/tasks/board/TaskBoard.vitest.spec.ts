import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { defineComponent, h } from 'vue';
import { createI18n } from 'vue-i18n';
import { useBoardColumns } from 'src/application/dynamic/queries/task-board-queries';
import zhTW from 'src/i18n/zh-TW.json';
import type { InternalAxiosRequestConfig } from 'axios';
import { http } from 'src/services/api/http';
import { bodyOf, HttpFail, mockHttp, type Call } from 'src/test-utils/mock-http';
import { assigneeBoardWire, assigneeConfigWire, boardWire, cardWire, columnWire, dueBoardWire, nineStatusLikeColumns } from './board-fixtures';
import TaskBoard from './TaskBoard.vue';

let resize: ((width: number) => void) | undefined;
let calls: Call[];

/** 以假的 ResizeObserver 餵容器寬度；jsdom 沒有版面，clientWidth 永遠是 0。 */
function stubResizeObserver() {
  vi.stubGlobal('ResizeObserver', class {
    private cb: ResizeObserverCallback;
    constructor(cb: ResizeObserverCallback) { this.cb = cb; resize = (width) => cb([{ contentRect: { width } } as ResizeObserverEntry], this as unknown as ResizeObserver); }
    observe() {}
    unobserve() {}
    disconnect() {}
  });
}

/** 和頁面一樣由 useBoardColumns 供應資料，「載入更多」才會經由快取回到畫面上。 */
async function mountBoard(wire: Record<string, unknown>, width: number, pageReply: unknown = null, moveReply: unknown = null) {
  const source = {};
  calls = mockHttp((c) => (c.url === '/task-board/columns' ? wire : /\/move$/.test(c.url) ? moveReply : pageReply));
  const Host = defineComponent({
    setup() {
      const q = useBoardColumns(source);
      return () => (q.data.value ? h(TaskBoard, { board: q.data.value, source }) : null);
    },
  });
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(Host, { attachTo: document.body, global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] } });
  await flushPromises();
  resize?.(width);
  await flushPromises();
  return { w };
}
const shownKeys = (w: ReturnType<typeof mount>) => w.findAll('[data-board-column]').map((e) => e.attributes('data-board-column'));

describe('TaskBoard', () => {
  beforeEach(() => { resize = undefined; stubResizeObserver(); });
  afterEach(() => { vi.unstubAllGlobals(); });

  it('TaskBoard renders columns with totals and load-more', async () => {
    const wire = boardWire({ columns: [
      columnWire({ key: 'todo', total: 52, hasMore: true, cards: [cardWire({ id: 1 })] }),
      columnWire({ key: 'inProgress', total: 1, cards: [cardWire({ id: 3, title: '整理名片' })] }),
      columnWire({ key: 'done', total: 0, cards: [] }),
    ] });
    const { w } = await mountBoard(wire, 1126, { key: 'todo', total: 52, cards: [cardWire({ id: 1 }), cardWire({ id: 99, title: '第二頁' })], hasMore: false, truncated: false });
    expect(shownKeys(w)).toEqual(['todo', 'inProgress', 'done']);
    expect(w.get('[data-testid="board-column-todo"] [data-testid="board-column-label"]').text()).toBe('待辦');
    expect(w.get('[data-testid="board-column-todo"] [data-testid="board-column-total"]').text()).toBe('52');
    expect(w.get('[data-testid="board-column-done"] [data-testid="board-column-empty"]').text()).toBe('沒有任務');
    expect(w.findAll('[data-testid="board-column-todo"] [data-testid^="board-card-"]').filter((e) => /card-\d+$/.test(e.attributes('data-testid')!))).toHaveLength(1);
    await w.get('[data-testid="board-load-more"]').trigger('click');
    await flushPromises();
    const page = calls.find((c) => /\/page$/.test(c.url))!;
    expect(page.url).toBe('/task-board/columns/todo/page');
    expect(bodyOf(page)).toMatchObject({ offset: 1 });
    // 第二頁接在後面、重複的卡片不重複，載入更多鈕消失
    expect(w.findAll('[data-testid="board-column-todo"] [data-testid^="board-card-"]').filter((e) => /card-\d+$/.test(e.attributes('data-testid')!)).map((e) => e.attributes('data-testid')))
      .toEqual(['board-card-1', 'board-card-99']);
    expect(w.find('[data-testid="board-load-more"]').exists()).toBe(false);
    w.unmount();
  });

  it('shows the truncated hint', async () => {
    const wire = boardWire({ columns: [columnWire({ key: 'todo', total: 520, truncated: true, hasMore: false, cards: Array.from({ length: 500 }, (_, i) => cardWire({ id: i + 1 })) })] });
    const { w } = await mountBoard(wire, 1126);
    expect(w.get('[data-testid="board-truncated"]').text()).toBe('還有 20 筆，請加上篩選或改用清單');
    expect(w.find('[data-testid="board-load-more"]').exists()).toBe(false);
    w.unmount();
  });

  it('phone shows column chips and one column', async () => {
    const { w } = await mountBoard(boardWire(), 358);
    expect(shownKeys(w)).toEqual(['todo']);
    expect(w.find('[data-testid="board-prev"]').exists()).toBe(false);
    expect(w.get('[data-testid="board-chip-inProgress"]').text()).toBe('進行中 (1)');
    expect(w.get('[data-testid="board-chip-todo"]').attributes('aria-pressed')).toBe('true');
    await w.get('[data-testid="board-chip-inProgress"]').trigger('click');
    expect(shownKeys(w)).toEqual(['inProgress']);
    expect(w.get('[data-testid="board-chip-inProgress"]').attributes('aria-pressed')).toBe('true');
    w.unmount();
  });

  it('pager moves by one screen', async () => {
    const { w } = await mountBoard(boardWire({ config: assigneeConfigWire(), columns: nineStatusLikeColumns() }), 1126);
    expect(w.get('[data-testid="board-grid"]').attributes('style')).toContain('repeat(4, minmax(0, 1fr))');
    expect(shownKeys(w)).toEqual(['user:u1', 'user:u2', 'user:u3', 'user:u4']);
    expect(w.get('[data-testid="board-range"]').text()).toBe('第 1–4 欄，共 9 欄');
    expect(w.get('[data-testid="board-prev"]').attributes('disabled')).toBeDefined();
    await w.get('[data-testid="board-next"]').trigger('click');
    expect(shownKeys(w)).toEqual(['user:u5', 'user:u6', 'user:u7', 'user:u8']);
    expect(w.get('[data-testid="board-range"]').text()).toBe('第 5–8 欄，共 9 欄');
    await w.get('[data-testid="board-next"]').trigger('click');
    expect(shownKeys(w)).toEqual(['user:u9']);
    expect(w.get('[data-testid="board-range"]').text()).toBe('第 9 欄，共 9 欄');
    expect(w.get('[data-testid="board-next"]').attributes('disabled')).toBeDefined();
    await w.get('[data-testid="board-prev"]').trigger('click');
    expect(w.get('[data-testid="board-range"]').text()).toBe('第 5–8 欄，共 9 欄');
    w.unmount();
  });

  it('shows no pager when every column fits, and follows the container width', async () => {
    const { w } = await mountBoard(boardWire(), 1126);
    expect(w.find('[data-testid="board-pager"]').exists()).toBe(false);
    expect(shownKeys(w)).toHaveLength(3);
    resize?.(600);
    await flushPromises();
    expect(shownKeys(w)).toHaveLength(2);
    expect(w.find('[data-testid="board-pager"]').exists()).toBe(true);
    w.unmount();
  });

  it('never shows more columns than the width allows at 390, 820, 1366 and 1920', async () => {
    for (const [viewport, expected] of [[390, 1], [820, 3], [1366, 4], [1920, 6]] as const) {
      const { w } = await mountBoard(boardWire({ config: assigneeConfigWire(), columns: nineStatusLikeColumns() }), viewport - (viewport >= 1024 ? 208 : 0) - 32);
      expect(shownKeys(w), String(viewport)).toHaveLength(expected);
      w.unmount();
    }
  });

  it('fills the width when every column fits, and keeps the fixed count (last group left open) when it has to page', async () => {
    const fits = await mountBoard(boardWire(), 1126);
    expect(fits.w.get('[data-testid="board-grid"]').attributes('style')).toContain('repeat(3, minmax(0, 1fr))');
    fits.w.unmount();
    // 820 viewport: three status columns fit at once, no pager
    const tablet = await mountBoard(boardWire(), 788);
    expect(tablet.w.get('[data-testid="board-grid"]').attributes('style')).toContain('repeat(3, minmax(0, 1fr))');
    expect(shownKeys(tablet.w)).toHaveLength(3);
    expect(tablet.w.find('[data-testid="board-pager"]').exists()).toBe(false);
    tablet.w.unmount();
    // 9 columns, 4 per screen: the last group has one column but the grid still has 4 tracks
    const { w } = await mountBoard(boardWire({ config: assigneeConfigWire(), columns: nineStatusLikeColumns() }), 1126);
    await w.get('[data-testid="board-next"]').trigger('click');
    await w.get('[data-testid="board-next"]').trigger('click');
    expect(shownKeys(w)).toHaveLength(1);
    expect(w.get('[data-testid="board-grid"]').attributes('style')).toContain('repeat(4, minmax(0, 1fr))');
    w.unmount();
  });

  it('column totals read as a count, columns are labelled groups and chips cut long names', async () => {
    const { w } = await mountBoard(boardWire(), 1126);
    const total = w.get('[data-testid="board-column-todo"] [data-testid="board-column-total"]');
    expect(total.attributes('aria-label')).toBe('共 2 筆');
    const col = w.get('[data-testid="board-column-todo"]');
    expect(col.element.tagName).toBe('DIV');
    expect(col.attributes('role')).toBe('group');
    expect(col.attributes('aria-label')).toBe('待辦');
    w.unmount();
    const phone = await mountBoard(boardWire(), 358);
    const chip = phone.w.get('[data-testid="board-chip-todo"]');
    expect(chip.attributes('title')).toBe('待辦 (2)');
    expect(chip.get('.ellipsis').text()).toBe('待辦 (2)');
    phone.w.unmount();
  });

  it('two columns loading more at once keep their own spinners', async () => {
    const wire = boardWire({ columns: [
      columnWire({ key: 'todo', total: 60, hasMore: true, cards: [cardWire({ id: 1 })] }),
      columnWire({ key: 'inProgress', total: 60, hasMore: true, cards: [cardWire({ id: 3 })] }),
    ] });
    const { w } = await mountBoard(wire, 1126, null);
    // hold the page requests until the test releases them
    const release: Record<string, () => void> = {};
    const base = http.defaults.adapter as (c: InternalAxiosRequestConfig) => Promise<unknown>;
    http.defaults.adapter = ((config: InternalAxiosRequestConfig) => {
      const key = /columns\/([^/]+)\/page$/.exec(String(config.url))?.[1];
      if (!key) return base(config);
      return new Promise((resolve) => {
        release[key] = () => resolve({ data: { key, total: 60, cards: [cardWire({ id: key === 'todo' ? 11 : 33 })], hasMore: false, truncated: false }, status: 200, statusText: 'OK', headers: {}, config });
      });
    }) as never;
    const loading = (key: string) => w.get(`[data-testid="board-column-${key}"] [data-testid="board-load-more"]`).find('.q-spinner').exists();
    await w.get('[data-testid="board-column-todo"] [data-testid="board-load-more"]').trigger('click');
    await w.get('[data-testid="board-column-inProgress"] [data-testid="board-load-more"]').trigger('click');
    await flushPromises();
    expect([loading('todo'), loading('inProgress')]).toEqual([true, true]);
    release.todo();
    await flushPromises();
    expect(w.find('[data-testid="board-column-todo"] [data-testid="board-load-more"]').exists()).toBe(false);
    expect(loading('inProgress')).toBe(true);
    release.inProgress();
    await flushPromises();
    w.unmount();
  });

  it('labels columns by kind: statuses, due buckets, unassigned, inactive users; and notes omitted assignees', async () => {
    const status = await mountBoard(boardWire(), 1126);
    expect(status.w.findAll('[data-testid="board-column-label"]').map((e) => e.text())).toEqual(['待辦', '進行中', '已完成']);
    status.w.unmount();
    const due = await mountBoard(dueBoardWire(), 1680);
    expect(due.w.findAll('[data-testid="board-column-label"]').map((e) => e.text())).toEqual(['逾期', '今天', '本週', '之後', '無到期日']);
    due.w.unmount();
    const who = await mountBoard({ ...assigneeBoardWire(), omittedAssignees: 3 }, 1126);
    expect(who.w.findAll('[data-testid="board-column-label"]').map((e) => e.text())).toEqual(['未指派', '業務一', '老王（已停用）']);
    expect(who.w.get('[data-testid="board-omitted"]').text()).toBe('還有 3 位負責人未列出');
    who.w.unmount();
  });

  it('opens a card by emitting its id', async () => {
    const { w } = await mountBoard(boardWire(), 1126);
    await w.get('[data-testid="board-card-open-3"]').trigger('click');
    expect(w.findComponent(TaskBoard).emitted('open')).toEqual([[3]]);
    w.unmount();
  });
});

// ---- 拖拉、「移到…」、確認與樂觀更新（Task 9） ----
const moveCalls = () => calls.filter((c) => /\/move$/.test(c.url));
const ids = (w: ReturnType<typeof mount>, col: string) => w.findAll(`[data-testid="board-column-${col}"] [data-testid^="board-card-"]`)
  .map((e) => e.attributes('data-testid')!).filter((t) => /card-\d+$/.test(t)).map((t) => Number(t.replace('board-card-', '')));
const live = (w: ReturnType<typeof mount>) => w.get('[data-testid="board-live"]').text();
const toasts = () => [...document.body.querySelectorAll('.q-notification')].map((n) => n.textContent ?? '');

const settle = async () => { await flushPromises(); await new Promise((r) => setTimeout(r, 30)); await flushPromises(); };

/** 以滑鼠把卡片拖到指定欄放開；elementFromPoint 在 jsdom 沒有版面，這裡指定指標底下是哪一欄的元素。 */
async function dragTo(w: ReturnType<typeof mount>, cardId: number, columnKey: string | null) {
  const target = columnKey === null ? null : w.get(`[data-testid="board-column-${columnKey}"]`).element;
  document.elementFromPoint = (() => target) as never;
  const fire = (type: string, x: number, y: number, on: EventTarget = window, extra: Record<string, unknown> = {}) => {
    const e = new MouseEvent(type, { clientX: x, clientY: y, buttons: 1, bubbles: true });
    Object.assign(e, { pointerType: 'mouse', pointerId: 1, isPrimary: true }, extra);
    on.dispatchEvent(e);
  };
  fire('pointerdown', 10, 10, w.get(`[data-testid="board-card-${cardId}"]`).element);
  fire('pointermove', 80, 40);
  await flushPromises();
  return {
    drop: async () => { fire('pointerup', 80, 40); await flushPromises(); },
    fire,
  };
}

/** 讓 /move 請求停住，由測試決定何時、怎麼回；其他請求照舊。 */
function holdMove() {
  const base = http.defaults.adapter as (c: InternalAxiosRequestConfig) => Promise<unknown>;
  const all: { ok: (data: unknown) => void; fail: (status: number, data?: unknown) => void }[] = [];
  http.defaults.adapter = ((config: InternalAxiosRequestConfig) => {
    if (!/\/move$/.test(String(config.url))) return base(config);
    calls.push({ method: String(config.method), url: String(config.url), params: config.params, data: config.data });
    return new Promise((resolve, reject) => {
      all.push({
        ok: (data) => resolve({ data, status: 200, statusText: 'OK', headers: {}, config }),
        fail: (status, data) => reject({ config, response: { status, data, headers: {}, config } }),
      });
    });
  }) as never;
  // ok / fail answer the latest request; all[i] answers the i-th
  return { all, ok: (d: unknown) => all.at(-1)!.ok(d), fail: (s: number, d?: unknown) => all.at(-1)!.fail(s, d) };
}
const movedCard = (o: Record<string, unknown> = {}) => ({ visible: true, card: cardWire({ id: 1, status: 'inProgress', version: '20261102000000000', ...o }), undoToken: null });

// Notify keeps one document-level container for the whole file, so the body is never reset in this file; every test unmounts its own board.
describe('TaskBoard moving', () => {
  beforeEach(() => { resize = undefined; stubResizeObserver(); });
  afterEach(() => { vi.unstubAllGlobals(); });

  it('TaskBoard moves optimistically and rolls back on 400', async () => {
    const { w } = await mountBoard(boardWire(), 1126);
    const held = holdMove();
    const d = await dragTo(w, 1, 'inProgress');
    await d.drop();
    // 還沒回應就已經在目標欄
    expect(ids(w, 'inProgress')).toEqual([1, 3]);
    expect(ids(w, 'todo')).toEqual([2]);
    expect(w.get('[data-testid="board-column-inProgress"] [data-testid="board-column-total"]').text()).toBe('2');
    expect(bodyOf(moveCalls()[0])).toEqual({ groupBy: 'status', toColumn: 'inProgress', expectedVersion: '20261101020000000' });
    expect(moveCalls()[0].url).toBe('/tasks/1/move');
    held.fail(400, { title: 'x', errors: { toColumn: ['SERVER-TEXT 看板欄不正確'] } });
    await settle();
    expect(ids(w, 'todo')).toEqual([1, 2]);
    expect(ids(w, 'inProgress')).toEqual([3]);
    expect(toasts().some((t) => t.includes('這一欄不能放'))).toBe(true);
    expect(toasts().some((t) => t.includes('SERVER-TEXT'))).toBe(false);
    w.unmount();
  });

  it('announces a successful move once through the single aria-live node', async () => {
    const { w } = await mountBoard(boardWire(), 1126, null, movedCard());
    // Quasar 的徽章預設是 role=status（隱含 aria-live），欄頭總數改成 img，整個看板只留下這一個播報節點
    expect(w.findAll('[aria-live], [role="status"], [role="alert"]')).toHaveLength(1);
    const d = await dragTo(w, 1, 'inProgress');
    await d.drop();
    await settle();
    expect(live(w)).toBe('已移到「進行中」');
    expect(w.findAll('[aria-live], [role="status"], [role="alert"]')).toHaveLength(1);
    w.unmount();
  });

  it('keeps a single aria-live node even when the pager shows "columns 1-4 of 9"', async () => {
    const { w } = await mountBoard(boardWire({ config: assigneeConfigWire(), columns: nineStatusLikeColumns() }), 1126);
    expect(w.find('[data-testid="board-range"]').exists()).toBe(true);
    expect(w.findAll('[aria-live], [role="status"], [role="alert"]')).toHaveLength(1);
    w.unmount();
  });

  const announcing = (text: string) => [...document.body.querySelectorAll('[role="alert"], [role="status"], [aria-live]')].filter((n) => (n.textContent ?? '').includes(text)).length;

  it('a failed move is read out once: by the notification, not also by the live region', async () => {
    const { w } = await mountBoard(boardWire(), 1126, null, new HttpFail(403, { message: 'SERVER-TEXT' }));
    const before = announcing('移動失敗');
    const d = await dragTo(w, 1, 'inProgress');
    await d.drop();
    await settle();
    expect(announcing('移動失敗') - before).toBe(1);
    expect(live(w)).toBe('');
    w.unmount();
  });

  it('moving out of scope is read out once, by the notification', async () => {
    const wire = assigneeBoardWire();
    (wire.columns as { key: string }[]).splice(2, 1, columnWire({ key: 'user:sales02', kind: 'user', userId: 'sales02', name: '業務二', inactive: false, total: 0, cards: [] }));
    const { w } = await mountBoard(wire, 1126, null, { visible: false, card: null, undoToken: 'tok' });
    const before = announcing('已移出你的範圍');
    const d = await dragTo(w, 1, 'user:sales02');
    await d.drop();
    await settle();
    expect(announcing('已移出你的範圍') - before).toBe(1);
    expect(live(w)).toBe('');
    w.unmount();
  });

  it('the same result twice in a row is announced again (the live node is rewritten, not left untouched)', async () => {
    const wire = boardWire({ columns: [
      columnWire({ key: 'todo', total: 2, cards: [cardWire({ id: 1 }), cardWire({ id: 2 })] }), columnWire({ key: 'inProgress', total: 0, cards: [] }),
    ] });
    const { w } = await mountBoard(wire, 1126, null, movedCard());
    let d = await dragTo(w, 1, 'inProgress');
    await d.drop();
    await settle();
    const first = w.get('[data-testid="board-live"]').element.firstChild;
    expect(live(w)).toBe('已移到「進行中」');
    d = await dragTo(w, 2, 'inProgress');
    await d.drop();
    await settle();
    expect(live(w)).toBe('已移到「進行中」');
    expect(w.get('[data-testid="board-live"]').element.firstChild).not.toBe(first);
    w.unmount();
  });

  it('dropping twice on a column that cannot take the card announces the reason both times', async () => {
    const { w } = await mountBoard(dueWire({}), 1680);
    let d = await dragTo(w, 1, 'overdue');
    await d.drop();
    const first = w.get('[data-testid="board-live"]').element.firstChild;
    await settle();
    expect(live(w)).toBe('不能改成過去的日期，請開任務詳細');
    d = await dragTo(w, 1, 'overdue');
    await d.drop();
    await settle();
    expect(live(w)).toBe('不能改成過去的日期，請開任務詳細');
    expect(w.get('[data-testid="board-live"]').element.firstChild).not.toBe(first);
    w.unmount();
  });

  it('TaskBoard 409 refetches and shows the stale message', async () => {
    const { w } = await mountBoard(boardWire(), 1126, null, new HttpFail(409, { code: 'stale', message: 'SERVER-TEXT' }));
    const before = calls.filter((c) => c.url === '/task-board/columns').length;
    const d = await dragTo(w, 1, 'inProgress');
    await d.drop();
    await settle();
    expect(toasts().some((t) => t.includes('任務已被別人修改'))).toBe(true);
    expect(toasts().some((t) => t.includes('SERVER-TEXT'))).toBe(false);
    expect(calls.filter((c) => c.url === '/task-board/columns').length).toBeGreaterThan(before);
    expect(ids(w, 'todo')).toEqual([1, 2]);
    w.unmount();
  });

  it('TaskBoard visible false shows the moved-out notice', async () => {
    const wire = assigneeBoardWire();
    (wire.columns as { key: string }[]).splice(2, 1, columnWire({ key: 'user:sales02', kind: 'user', userId: 'sales02', name: '業務二', inactive: false, total: 0, cards: [] }));
    const { w } = await mountBoard(wire, 1126, null, { visible: false, card: null, undoToken: 'tok' });
    const d = await dragTo(w, 1, 'user:sales02');
    await d.drop();
    await settle();
    expect(moveCalls()).toHaveLength(1);
    expect(bodyOf(moveCalls()[0])).toMatchObject({ groupBy: 'assignee', toColumn: 'user:sales02' });
    expect(toasts().some((t) => t.includes('已移出你的範圍') && t.includes('復原'))).toBe(true);
    // 卡片不會出現在目標欄（從畫面拿掉的細節由 useMoveTask 的 spec 驗證；這裡的假伺服器每次重抓都回同一份看板）
    expect(ids(w, 'user:sales02')).toEqual([]);
    w.unmount();
  });

  it('TaskBoard cancel column asks for confirmation', async () => {
    const wire = boardWire({ columns: [columnWire({ key: 'todo', total: 1, cards: [cardWire({ id: 1 })] }), columnWire({ key: 'cancelled', total: 0, cards: [] })] });
    const { w } = await mountBoard(wire, 1126, null, movedCard({ status: 'cancelled' }));
    let d = await dragTo(w, 1, 'cancelled');
    await d.drop();
    await settle();
    expect(moveCalls()).toHaveLength(0);
    const dialog = document.body.querySelector('[data-testid="move-confirm"]')!;
    expect(dialog.textContent).toContain('取消這個任務？');
    expect(dialog.textContent).toContain('規則產生的任務取消後不會自動恢復');
    expect(ids(w, 'todo')).toEqual([1]);
    document.body.querySelector<HTMLElement>('[data-testid="move-confirm-cancel"]')!.click();
    await settle();
    expect(moveCalls()).toHaveLength(0);
    d = await dragTo(w, 1, 'cancelled');
    await d.drop();
    await settle();
    document.body.querySelector<HTMLElement>('[data-testid="move-confirm-ok"]')!.click();
    await settle();
    expect(moveCalls()).toHaveLength(1);
    expect(bodyOf(moveCalls()[0])).toMatchObject({ groupBy: 'status', toColumn: 'cancelled' });
    w.unmount();
  });

  function dueWire(cardOverrides: Record<string, unknown>) {
    const wire = dueBoardWire() as { columns: { key: string; cards: unknown[]; total: number }[] };
    const today = wire.columns.find((c) => c.key === 'today')!;
    today.cards = [cardWire({ id: 1, dueDate: '2026-11-17', dueToday: true, ...cardOverrides })];
    today.total = 1;
    return wire as unknown as Record<string, unknown>;
  }

  it('TaskBoard clearing a rule task due date shows the sync warning', async () => {
    const { w } = await mountBoard(dueWire({ fromRule: true }), 1680, null, movedCard({ dueDate: null }));
    const d = await dragTo(w, 1, 'none');
    await d.drop();
    await settle();
    expect(moveCalls()).toHaveLength(0);
    const dialog = document.body.querySelector('[data-testid="move-confirm"]')!;
    expect(dialog.textContent).toContain('清空後不再隨記錄日期同步');
    document.body.querySelector<HTMLElement>('[data-testid="move-confirm-ok"]')!.click();
    await settle();
    expect(bodyOf(moveCalls()[0])).toMatchObject({ groupBy: 'dueBucket', toColumn: 'none' });
    w.unmount();
  });

  it('clearing the due date of a task that is not from a rule moves straight away', async () => {
    const { w } = await mountBoard(dueWire({ fromRule: false }), 1680, null, movedCard({ dueDate: null }));
    const d = await dragTo(w, 1, 'none');
    await d.drop();
    await settle();
    expect(document.body.querySelector('[data-testid="move-confirm"]')).toBeNull();
    expect(moveCalls()).toHaveLength(1);
    w.unmount();
  });

  it('dropping on Later opens the date picker; cancel puts nothing on the wire, a pick sends the date', async () => {
    const { w } = await mountBoard(dueWire({}), 1680, null, movedCard({ dueDate: '2026-12-01' }));
    let d = await dragTo(w, 1, 'later');
    await d.drop();
    await settle();
    expect(document.body.querySelector('[data-testid="due-pick"]')).not.toBeNull();
    document.body.querySelector<HTMLElement>('[data-testid="due-pick-cancel"]')!.click();
    await settle();
    expect(moveCalls()).toHaveLength(0);
    expect(ids(w, 'today')).toEqual([1]);
    d = await dragTo(w, 1, 'later');
    await d.drop();
    await settle();
    document.body.querySelector<HTMLElement>('[data-testid="due-pick-ok"]')!.click();
    await settle();
    // 預設是伺服器給的 laterDefault
    expect(bodyOf(moveCalls()[0])).toMatchObject({ groupBy: 'dueBucket', toColumn: 'later', dueDate: '2026-11-29' });
    w.unmount();
  });

  it('shows the reason on blocked columns while dragging and says it when a card is dropped there', async () => {
    const { w } = await mountBoard(dueWire({}), 1680);
    const d = await dragTo(w, 1, 'overdue');
    expect(w.get('[data-testid="board-column-overdue"]').classes()).toContain('drop-blocked');
    expect(w.get('[data-testid="board-column-overdue"] [data-testid="board-column-drop-reason"]').text()).toBe('不能改成過去的日期，請開任務詳細');
    expect(w.get('[data-testid="board-column-later"]').classes()).toContain('drop-ok');
    expect(w.find('[data-testid="board-ghost"]').exists()).toBe(true);
    await d.drop();
    expect(moveCalls()).toHaveLength(0);
    expect(live(w)).toBe('不能改成過去的日期，請開任務詳細');
    expect(w.find('[data-testid="board-ghost"]').exists()).toBe(false);
    expect(w.find('[data-testid="board-column-drop-reason"]').exists()).toBe(false);
    w.unmount();
  });

  it('Esc during a drag sends nothing', async () => {
    const { w } = await mountBoard(boardWire(), 1126);
    const d = await dragTo(w, 1, 'inProgress');
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    await flushPromises();
    expect(w.find('[data-testid="board-ghost"]').exists()).toBe(false);
    await d.drop();
    expect(moveCalls()).toHaveLength(0);
    w.unmount();
  });

  it('TaskBoard non-draggable card has no move-to button', async () => {
    const wire = boardWire({ columns: [columnWire({ key: 'todo', total: 3, cards: [
      cardWire({ id: 1 }), cardWire({ id: 2, frozen: true }), cardWire({ id: 3, canEdit: false, canReassign: false }),
    ] }), columnWire({ key: 'done', total: 0, cards: [] })] });
    const { w } = await mountBoard(wire, 1126);
    expect(w.find('[data-testid="board-card-move-1"]').exists()).toBe(true);
    expect(w.find('[data-testid="board-card-move-2"]').exists()).toBe(false);
    expect(w.find('[data-testid="board-card-move-3"]').exists()).toBe(false);
    // 拖也拖不動
    const d = await dragTo(w, 2, 'done');
    expect(w.find('[data-testid="board-ghost"]').exists()).toBe(false);
    await d.drop();
    expect(moveCalls()).toHaveLength(0);
    w.unmount();
  });

  it('the move-to menu moves a card to a column that is not on screen', async () => {
    // 九欄、一屏四欄：第 9 欄不在畫面上，拖不到，但「移到…」選得到
    const cols = nineStatusLikeColumns();
    (cols[0] as { cards: unknown[]; total: number }).cards = [cardWire({ id: 1, assigneeUserId: 'u1' })];
    const { w } = await mountBoard(boardWire({ config: assigneeConfigWire(), columns: cols }), 1126, null, movedCard({ assigneeUserId: 'u9' }));
    expect(shownKeys(w)).not.toContain('user:u9');
    await w.get('[data-testid="board-card-move-1"]').trigger('click');
    await flushPromises();
    document.body.querySelector<HTMLElement>('[data-testid="move-item-user:u9"]')!.click();
    await settle();
    expect(bodyOf(moveCalls()[0])).toMatchObject({ groupBy: 'assignee', toColumn: 'user:u9' });
    expect(live(w)).toBe('已移到「同事9」');
    w.unmount();
  });

  it('on a phone there is no dragging, only the bottom sheet', async () => {
    const { w } = await mountBoard(boardWire(), 358, null, movedCard());
    const d = await dragTo(w, 1, 'todo');
    expect(w.find('[data-testid="board-ghost"]').exists()).toBe(false);
    await d.drop();
    await w.get('[data-testid="board-card-move-1"]').trigger('click');
    await flushPromises();
    expect(document.body.querySelector('[data-testid="move-sheet"]')).not.toBeNull();
    document.body.querySelector<HTMLElement>('[data-testid="move-item-inProgress"]')!.click();
    await settle();
    expect(bodyOf(moveCalls()[0])).toMatchObject({ toColumn: 'inProgress' });
    w.unmount();
  });

  it.each([
    ['400 on the column', 400, { title: 'x', errors: { toColumn: ['SERVER-TEXT'] } }, '這一欄不能放'],
    ['400 on the due date', 400, { title: 'x', errors: { dueDate: ['SERVER-TEXT'] } }, '日期必須晚於 2026-11-22'],
    ['400 on something else', 400, { title: 'x', errors: { groupBy: ['SERVER-TEXT'] } }, '移動失敗'],
    ['403', 403, { message: 'SERVER-TEXT' }, '移動失敗'],
    ['404', 404, { message: 'SERVER-TEXT' }, '找不到這個任務，或你沒有權限查看'],
    ['409 without a code (record locked)', 409, { message: 'SERVER-TEXT' }, '相關記錄已鎖定，不能移動'],
    ['500', 500, { message: 'SERVER-TEXT' }, '移動失敗'],
  ])('failure messages come from the interface language, never the server text: %s', async (_name, status, body, expected) => {
    const { w } = await mountBoard(boardWire(), 1126, null, new HttpFail(status, body));
    const d = await dragTo(w, 1, 'inProgress');
    await d.drop();
    await settle();
    expect(toasts().some((t) => t.includes(expected))).toBe(true);
    expect(toasts().some((t) => t.includes('SERVER-TEXT'))).toBe(false);
    expect(ids(w, 'todo')).toEqual([1, 2]);
    w.unmount();
  });

  it('a card that was just dragged does not open its detail when the pointer is released', async () => {
    const { w } = await mountBoard(boardWire(), 1126, null, movedCard());
    const d = await dragTo(w, 1, 'inProgress');
    // the browser sends the click right after pointerup, before anything else runs
    const open = w.get('[data-testid="board-card-open-1"]').element;
    const click = () => open.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
    d.fire('pointerup', 80, 40);
    click();
    await flushPromises();
    expect(w.findComponent(TaskBoard).emitted('open')).toBeUndefined();
    // the next click is an ordinary click again
    await new Promise((r) => setTimeout(r, 10));
    await w.get('[data-testid="board-card-open-1"]').trigger('click');
    expect(w.findComponent(TaskBoard).emitted('open')).toEqual([[1]]);
    w.unmount();
  });

  it('a second finger releasing over another column does not drop the card being dragged', async () => {
    const { w } = await mountBoard(boardWire(), 1126);
    const d = await dragTo(w, 1, 'todo');
    document.elementFromPoint = (() => w.get('[data-testid="board-column-done"]').element) as never;
    d.fire('pointermove', 300, 40, window, { pointerId: 2, isPrimary: false });
    d.fire('pointerup', 300, 40, window, { pointerId: 2, isPrimary: false });
    await flushPromises();
    expect(moveCalls()).toHaveLength(0);
    expect(w.find('[data-testid="board-ghost"]').exists()).toBe(true);
    d.fire('pointercancel', 80, 40);
    await flushPromises();
    expect(w.find('[data-testid="board-ghost"]').exists()).toBe(false);
    expect(moveCalls()).toHaveLength(0);
    w.unmount();
  });

  it('a card whose move has not been answered yet cannot be moved again, by drag or by the menu', async () => {
    const { w } = await mountBoard(boardWire(), 1126);
    const held = holdMove();
    let d = await dragTo(w, 1, 'inProgress');
    await d.drop();
    expect(moveCalls()).toHaveLength(1);
    d = await dragTo(w, 1, 'done');
    expect(w.find('[data-testid="board-ghost"]').exists()).toBe(false);
    await d.drop();
    await w.get('[data-testid="board-card-move-1"]').trigger('click');
    await flushPromises();
    document.body.querySelector<HTMLElement>('[data-testid="move-item-done"]')!.click();
    await settle();
    expect(moveCalls()).toHaveLength(1);
    // another card is not held back
    d = await dragTo(w, 2, 'done');
    await d.drop();
    expect(moveCalls()).toHaveLength(2);
    held.all[0].ok(movedCard());
    held.all[1].ok(movedCard({ id: 2, status: 'done' }));
    await settle();
    // once it is answered the card can move again
    d = await dragTo(w, 1, 'done');
    await d.drop();
    expect(moveCalls()).toHaveLength(3);
    w.unmount();
  });

  it('a status board shows no move-to button on a card the user cannot edit, even when it can be reassigned', async () => {
    const wire = boardWire({ columns: [columnWire({ key: 'todo', total: 1, cards: [cardWire({ id: 1, canEdit: false, canReassign: true })] }), columnWire({ key: 'done', total: 0, cards: [] })] });
    const status = await mountBoard(wire, 1126);
    expect(status.w.find('[data-testid="board-card-move-1"]').exists()).toBe(false);
    status.w.unmount();
    const byAssignee = assigneeBoardWire();
    (byAssignee.columns as { cards: unknown[] }[])[1].cards = [cardWire({ id: 1, canEdit: false, canReassign: true })];
    const who = await mountBoard(byAssignee, 1126);
    expect(who.w.find('[data-testid="board-card-move-1"]').exists()).toBe(true);
    who.w.unmount();
  });

  describe('keyboard focus after a move-to', () => {
    /** 九欄一屏四欄；u1 欄有 cards 指定的卡，第 9 欄在畫面外。 */
    async function mountNine(cardIds: number[]) {
      const cols = nineStatusLikeColumns();
      (cols[0] as { cards: unknown[]; total: number }).cards = cardIds.map((id) => cardWire({ id, assigneeUserId: 'u1' }));
      (cols[0] as { total: number }).total = cardIds.length;
      const board = await mountBoard(boardWire({ config: assigneeConfigWire(), columns: cols }), 1126);
      return { ...board, held: holdMove() };
    }
    /** 用鍵盤路徑選欄；請求停在途中，這時看板是樂觀更新後的樣子。 */
    const pickFromMenu = async (w: ReturnType<typeof mount>, cardId: number, columnKey: string) => {
      (w.get(`[data-testid="board-card-move-${cardId}"]`).element as HTMLElement).focus();
      await w.get(`[data-testid="board-card-move-${cardId}"]`).trigger('click');
      await new Promise((r) => setTimeout(r, 50));
      document.body.querySelector<HTMLElement>(`[data-testid="move-item-${columnKey}"]`)!.click();
      await settle();
    };
    const moveButton = (w: ReturnType<typeof mount>, column: string, id: number) => w.get(`[data-testid="board-column-${column}"] [data-testid="board-card-move-${id}"]`).element;
    const moved = (id: number, user: string) => ({ visible: true, card: { ...cardWire({ id, assigneeUserId: user, version: 'v2' }) }, undoToken: null });

    it('lands on the next card when the card went to a column that is not on screen', async () => {
      const { w, held } = await mountNine([1, 2]);
      await pickFromMenu(w, 1, 'user:u9');
      expect(ids(w, 'user:u1')).toEqual([2]);
      expect(document.activeElement).toBe(moveButton(w, 'user:u1', 2));
      held.ok(moved(1, 'u9'));
      await settle();
      expect(document.activeElement).toBe(moveButton(w, 'user:u1', 2));
      w.unmount();
    });

    it('lands on the previous card when the moved card was the last one', async () => {
      const { w, held } = await mountNine([1, 2]);
      await pickFromMenu(w, 2, 'user:u9');
      expect(ids(w, 'user:u1')).toEqual([1]);
      expect(document.activeElement).toBe(moveButton(w, 'user:u1', 1));
      held.ok(moved(2, 'u9'));
      await settle();
      w.unmount();
    });

    it('lands on the column itself when it was the only card and the target is off screen', async () => {
      const { w, held } = await mountNine([1]);
      await pickFromMenu(w, 1, 'user:u9');
      const column = w.get('[data-testid="board-column-user:u1"]');
      expect(column.attributes('tabindex')).toBe('-1');
      expect(document.activeElement).toBe(column.element);
      held.ok(moved(1, 'u9'));
      await settle();
      w.unmount();
    });

    it('stays on the same card (now in its new column) when the target column is on screen', async () => {
      const { w, held } = await mountNine([1, 2]);
      await pickFromMenu(w, 1, 'user:u2');
      expect(ids(w, 'user:u2')).toEqual([1]);
      expect(document.activeElement).toBe(moveButton(w, 'user:u2', 1));
      held.ok(moved(1, 'u2'));
      await settle();
      w.unmount();
    });

    it('returns to the card in its old column when the move fails and the card rolls back', async () => {
      const { w, held } = await mountNine([1, 2]);
      await pickFromMenu(w, 1, 'user:u2');
      // while the request is out the focus is somewhere sensible; the user then tabs to another card
      (w.get('[data-testid="board-card-open-2"]').element as HTMLElement).focus();
      held.fail(400, { title: 'x', errors: { toColumn: ['x'] } });
      await settle();
      expect(ids(w, 'user:u1')).toEqual([1, 2]);
      // focus was moved on purpose, so the rollback leaves it alone
      expect(document.activeElement).toBe(w.get('[data-testid="board-card-open-2"]').element);
      w.unmount();
    });

    it('puts focus back on the rolled-back card when nothing else has focus', async () => {
      const { w, held } = await mountNine([1, 2]);
      await pickFromMenu(w, 1, 'user:u9');
      (document.activeElement as HTMLElement).blur();
      held.fail(400, { title: 'x', errors: { toColumn: ['x'] } });
      await settle();
      expect(ids(w, 'user:u1')).toEqual([1, 2]);
      expect(document.activeElement).toBe(moveButton(w, 'user:u1', 1));
      w.unmount();
    });

    it('keeps focus on the card after the refetch reorders the target column (moved into the first column)', async () => {
      const wire = boardWire({ columns: [
        columnWire({ key: 'todo', total: 2, cards: [cardWire({ id: 2 }), cardWire({ id: 3 })] }),
        columnWire({ key: 'inProgress', total: 1, cards: [cardWire({ id: 1, status: 'inProgress' })] }),
      ] });
      const { w } = await mountBoard(wire, 1126);
      const held = holdMove();
      // a real refetch answers well after the move itself does
      const held2 = http.defaults.adapter as (c: InternalAxiosRequestConfig) => Promise<unknown>;
      http.defaults.adapter = (async (c: InternalAxiosRequestConfig) => {
        if (c.url === '/task-board/columns') await new Promise((r) => setTimeout(r, 120));
        return held2(c);
      }) as never;
      // jsdom keeps focus when a node is re-inserted; real browsers drop it, so mimic that
      const realInsert = Node.prototype.insertBefore;
      const insertSpy = vi.spyOn(Node.prototype, 'insertBefore').mockImplementation(function (this: Node, node: Node, ref: Node | null) {
        const focused = document.activeElement;
        const lost = focused && node.contains(focused) && node.parentNode !== null;
        const out = realInsert.call(this, node, ref);
        if (lost) (focused as HTMLElement).blur();
        return out;
      } as never);
      await pickFromMenu(w, 1, 'todo');
      expect(ids(w, 'todo')).toEqual([1, 2, 3]);
      expect(document.activeElement).toBe(moveButton(w, 'todo', 1));
      // the server sorts the moved card below the others, so the refetch moves its DOM node
      const cols = wire.columns as { cards: unknown[]; total: number }[];
      cols[0].cards = [cardWire({ id: 2 }), cardWire({ id: 3 }), cardWire({ id: 1, version: 'v2' })];
      cols[0].total = 3;
      cols[1].cards = [];
      cols[1].total = 0;
      held.ok({ visible: true, card: cardWire({ id: 1, version: 'v2' }), undoToken: null });
      await settle();
      await new Promise((r) => setTimeout(r, 250));
      await flushPromises();
      expect(ids(w, 'todo')).toEqual([2, 3, 1]);
      insertSpy.mockRestore();
      expect(document.activeElement).toBe(moveButton(w, 'todo', 1));
      w.unmount();
    });

    it('follows the last moved card when a second card is moved right after the first', async () => {
      const wire = boardWire({ columns: [
        columnWire({ key: 'todo', total: 3, cards: [cardWire({ id: 1 }), cardWire({ id: 2 }), cardWire({ id: 3 })] }),
        columnWire({ key: 'inProgress', total: 0, cards: [] }),
        columnWire({ key: 'done', total: 0, cards: [] }),
      ] });
      const { w } = await mountBoard(wire, 1126);
      const held = holdMove();
      const cols = wire.columns as { cards: unknown[]; total: number }[];
      const pick = async (id: number, key: string) => {
        (w.get(`[data-testid="board-card-move-${id}"]`).element as HTMLElement).focus();
        await w.get(`[data-testid="board-card-move-${id}"]`).trigger('click');
        await new Promise((r) => setTimeout(r, 50));
        document.body.querySelector<HTMLElement>(`[data-testid="move-item-${key}"]`)!.click();
        await settle();
      };
      await pick(1, 'inProgress');
      cols[0].cards = [cardWire({ id: 2 }), cardWire({ id: 3 })];
      cols[0].total = 2;
      cols[1].cards = [cardWire({ id: 1, version: 'v2' })];
      cols[1].total = 1;
      held.all[0].ok({ visible: true, card: cardWire({ id: 1, version: 'v2' }), undoToken: null });
      await settle();
      expect(document.activeElement).toBe(moveButton(w, 'inProgress', 1));
      // still inside the follow window: open another card's menu and move it
      await pick(3, 'done');
      cols[0].cards = [cardWire({ id: 2 })];
      cols[0].total = 1;
      cols[2].cards = [cardWire({ id: 3, version: 'v2' })];
      cols[2].total = 1;
      held.all[1].ok({ visible: true, card: cardWire({ id: 3, version: 'v2' }), undoToken: null });
      await settle();
      expect(document.activeElement).toBe(moveButton(w, 'done', 3));
      w.unmount();
    });

    it('does not pull focus away from where the user has gone in the meantime', async () => {
      const { w, held } = await mountNine([1, 2]);
      await pickFromMenu(w, 1, 'user:u9');
      (w.get('[data-testid="board-card-open-2"]').element as HTMLElement).focus();
      held.ok(moved(1, 'u9'));
      await settle();
      expect(document.activeElement).toBe(w.get('[data-testid="board-card-open-2"]').element);
      w.unmount();
    });
  });
});
