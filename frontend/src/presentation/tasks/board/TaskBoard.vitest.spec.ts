import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { defineComponent, h } from 'vue';
import { createI18n } from 'vue-i18n';
import { useBoardColumns } from 'src/application/dynamic/queries/task-board-queries';
import zhTW from 'src/i18n/zh-TW.json';
import { bodyOf, mockHttp, type Call } from 'src/test-utils/mock-http';
import { assigneeBoardWire, boardWire, cardWire, columnWire, dueBoardWire, nineStatusLikeColumns } from './board-fixtures';
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
async function mountBoard(wire: Record<string, unknown>, width: number, pageReply: unknown = null) {
  const source = {};
  calls = mockHttp((c) => (c.url === '/task-board/columns' ? wire : pageReply));
  const Host = defineComponent({
    setup() {
      const q = useBoardColumns(source);
      return () => (q.data.value ? h(TaskBoard, { board: q.data.value, source }) : null);
    },
  });
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(Host, { attachTo: document.body, global: { plugins: [[quasar.Quasar, { components: quasar }], i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] } });
  await flushPromises();
  resize?.(width);
  await flushPromises();
  return { w };
}
const shownKeys = (w: ReturnType<typeof mount>) => w.findAll('[data-board-column]').map((e) => e.attributes('data-board-column'));

describe('TaskBoard', () => {
  beforeEach(() => { document.body.innerHTML = ''; resize = undefined; stubResizeObserver(); });
  afterEach(() => { vi.unstubAllGlobals(); document.body.innerHTML = ''; });

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
    const { w } = await mountBoard(boardWire({ columns: nineStatusLikeColumns() }), 1126);
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
    resize?.(788);
    await flushPromises();
    expect(shownKeys(w)).toHaveLength(2);
    expect(w.find('[data-testid="board-pager"]').exists()).toBe(true);
    w.unmount();
  });

  it('never shows more columns than the width allows at 390, 820, 1366 and 1920', async () => {
    for (const [viewport, expected] of [[390, 1], [820, 2], [1366, 4], [1920, 6]] as const) {
      const { w } = await mountBoard(boardWire({ columns: nineStatusLikeColumns() }), viewport - (viewport >= 1024 ? 208 : 0) - 32);
      expect(shownKeys(w), String(viewport)).toHaveLength(expected);
      w.unmount();
    }
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
    await w.get('[data-testid="board-card-3"]').trigger('click');
    expect(w.findComponent(TaskBoard).emitted('open')).toEqual([[3]]);
    w.unmount();
  });
});
