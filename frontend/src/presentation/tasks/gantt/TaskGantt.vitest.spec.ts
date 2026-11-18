import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import enUS from 'src/i18n/en-US.json';
import zhTW from 'src/i18n/zh-TW.json';
import { bodyOf, HttpFail, mockHttp, type Call } from 'src/test-utils/mock-http';
import { cardWire } from '../board/board-fixtures';
import { ganttWire } from './gantt-fixtures';
import TaskGantt from './TaskGantt.vue';

let calls: Call[];
const ganttCalls = () => calls.filter((c) => c.url === '/tasks/gantt');
const lastBody = () => bodyOf(ganttCalls().at(-1)) as { viewId: number | null; config: Record<string, unknown> | null; window: { from: string; to: string } | null };

async function mountGantt(reply: (body: ReturnType<typeof lastBody>) => unknown = () => ganttWire(), locale = 'zh-TW') {
  calls = mockHttp((c) => (c.url === '/tasks/gantt' ? reply(bodyOf(c) as ReturnType<typeof lastBody>) : []));
  const i18n = createI18n({ legacy: false, locale, messages: { 'zh-TW': zhTW, 'en-US': enUS } });
  const w = mount(TaskGantt, {
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], i18n, [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false, refetchOnWindowFocus: false } } }) }]] },
  });
  await flushPromises();
  return w;
}
const pctOf = (el: { attributes: (n: string) => string | undefined }, prop: 'left' | 'width') => Number(new RegExp(`${prop}: ([\\d.]+)%`).exec(el.attributes('style') ?? '')?.[1]);

/** 依刻度回對應的預設時間窗，像伺服器一樣。 */
function serverLike(body: ReturnType<typeof lastBody>) {
  const scale = (body.config?.scale as string | undefined) ?? 'week';
  const win = body.window ?? (scale === 'day' ? { from: '2026-11-16', to: '2026-11-29' } : scale === 'month' ? { from: '2026-11-01', to: '2027-04-30' } : { from: '2026-11-16', to: '2027-02-14' });
  return ganttWire({ window: win, ...(body.config ? { config: body.config } : {}) });
}

describe('TaskGantt', () => {
  beforeEach(() => { document.body.innerHTML = ''; });
  afterEach(() => { quasar.Screen.lt.md = false; quasar.Screen.lt.sm = false; vi.unstubAllGlobals(); document.body.innerHTML = ''; });

  it('opens the default Gantt chart: no view id, no config, no window', async () => {
    const w = await mountGantt();
    expect(ganttCalls()).toHaveLength(1);
    expect(lastBody()).toEqual({ viewId: null, config: null, window: null });
    expect(w.get('[data-testid="gantt-range"]').text()).toBe('2026/11/16 – 2027/02/14');
  });

  it('G51 scale switch re-anchors on the current window start; next shifts by one screen; today drops the window', async () => {
    const w = await mountGantt(serverLike);
    await w.get('[data-testid="gantt-scale-day"]').trigger('click');
    await flushPromises();
    expect(lastBody().window).toEqual({ from: '2026-11-16', to: '2026-11-29' });
    expect(lastBody().config).toMatchObject({ scale: 'day', groupBy: 'parent' });
    expect(w.findAll('[data-testid="gantt-minor"]')).toHaveLength(14);
    await w.get('[data-testid="gantt-next"]').trigger('click');
    await flushPromises();
    expect(lastBody().window).toEqual({ from: '2026-11-30', to: '2026-12-13' });
    await w.get('[data-testid="gantt-scale-month"]').trigger('click');
    await flushPromises();
    expect(lastBody().window).toEqual({ from: '2026-11-01', to: '2027-04-30' });
    expect(w.findAll('[data-testid="gantt-major"]')).toHaveLength(6);
    expect(w.findAll('[data-testid="gantt-minor"]')).toHaveLength(0);
    await w.get('[data-testid="gantt-today"]').trigger('click');
    await flushPromises();
    expect(lastBody().window).toBeNull();
    expect(lastBody().config).toMatchObject({ scale: 'month' });
    await w.get('[data-testid="gantt-scale-week"]').trigger('click');
    await flushPromises();
    await w.get('[data-testid="gantt-today"]').trigger('click');
    await flushPromises();
    // 回到伺服器的刻度：只送預設甘特圖
    expect(lastBody()).toEqual({ viewId: null, config: null, window: null });
    expect(w.findAll('[data-testid="gantt-minor"]')).toHaveLength(13);
  });

  it('G52 draws the today line only while today is inside the window', async () => {
    const w = await mountGantt(serverLike);
    expect(w.find('[data-testid="gantt-today-line"]').exists()).toBe(true);
    expect(w.get('[data-testid="gantt-today-label"]').text()).toBe('今天');
    await w.get('[data-testid="gantt-next"]').trigger('click');
    await flushPromises();
    expect(lastBody().window).toEqual({ from: '2027-02-15', to: '2027-05-16' });
    expect(w.find('[data-testid="gantt-today-line"]').exists()).toBe(false);
  });

  it('G53 bar, milestone, overdue, done, clipped edges, and the dates in the bar name', async () => {
    const w = await mountGantt();
    const bar = w.get('[data-testid="gantt-bar-5"]');
    expect(bar.element.tagName).toBe('BUTTON');
    expect(bar.attributes('aria-label')).toBe('規劃活動內容：2026/11/16 – 2026/12/01');
    expect(pctOf(bar, 'left')).toBe(0);
    expect(pctOf(bar, 'width')).toBeCloseTo((16 / 91) * 100, 3);
    expect(w.find('[data-testid="gantt-bar-8"]').exists()).toBe(false);
    expect(w.get('[data-testid="gantt-milestone-8"]').attributes('aria-label')).toBe('寄合約：到期日 2026/11/20');
    expect(w.get('[data-testid="gantt-bar-9"]').classes()).toContain('is-overdue');
    expect(w.get('[data-testid="gantt-bar-6"]').classes()).toContain('is-closed');
    expect(w.get('[data-testid="gantt-title-6"]').classes()).toContain('text-strike');
    // 回覆報價從 11/10 開始，早於時間窗：左緣有箭頭，名稱裡是實際日期
    expect(w.get('[data-testid="gantt-bar-9"]').find('[data-testid="gantt-clip-start"]').exists()).toBe(true);
    expect(w.get('[data-testid="gantt-bar-9"]').attributes('aria-label')).toContain('2026/11/10');
    expect(w.get('[data-testid="gantt-bar-5"]').find('[data-testid="gantt-clip-start"]').exists()).toBe(false);
  });

  it('G53 clicking a bar, a milestone or a title opens the task', async () => {
    const w = await mountGantt();
    await w.get('[data-testid="gantt-bar-5"]').trigger('click');
    await w.get('[data-testid="gantt-milestone-8"]').trigger('click');
    await w.get('[data-testid="gantt-title-7"]').trigger('click');
    await w.get('[data-testid="gantt-unscheduled-20"]').trigger('click');
    expect(w.emitted('open')).toEqual([[5], [8], [7], [20]]);
  });

  it('G54 subtasks sit under the parent with its span and progress; collapsing hides them', async () => {
    const w = await mountGantt();
    const keys = () => w.findAll('[data-testid^="gantt-line-"]').map((e) => e.attributes('data-testid'));
    expect(keys()).toEqual(['gantt-line-t5', 'gantt-line-t6', 'gantt-line-t7', 'gantt-line-t8', 'gantt-line-t9']);
    expect(w.get('[data-testid="gantt-line-t6"] .gantt-left').classes()).toContain('is-child');
    expect(w.get('[data-testid="gantt-span-5"]').attributes('aria-label')).toBe('子任務跨度 2026/11/16 – 2026/12/09');
    expect(w.get('[data-testid="gantt-line-t5"]').get('[data-testid="subtask-progress-text"]').text()).toBe('1／3');
    const toggle = w.get('[data-testid="gantt-toggle-5"]');
    expect(toggle.attributes('aria-expanded')).toBe('true');
    await toggle.trigger('click');
    expect(keys()).toEqual(['gantt-line-t5', 'gantt-line-t8', 'gantt-line-t9']);
    expect(w.get('[data-testid="gantt-toggle-5"]').attributes('aria-label')).toBe('展開「規劃活動內容」的子任務');
  });

  it('G54 a parent outside the results is a faded title with its span; only a visible one can be opened', async () => {
    const rows = [
      cardWire({ id: 6, title: '子一', startDate: '2026-11-21', dueDate: '2026-11-25', level: 'child', parentTaskId: 5, parentTitle: '規劃活動內容' }),
      cardWire({ id: 11, title: '子二', dueDate: '2026-11-22', level: 'child', parentTaskId: 10, parentTitle: '主管的工作' }),
    ];
    const parents = [{ id: 5, title: '規劃活動內容', visible: true }, { id: 10, title: '主管的工作', visible: false }];
    const spans = [{ parentId: 5, from: '2026-11-21', to: '2026-11-30' }, { parentId: 10, from: '2026-11-22', to: '2026-11-22' }];
    const w = await mountGantt(() => ganttWire({ rows, parents, spans, total: 2 }));
    expect(w.find('[data-testid="gantt-bar-5"]').exists()).toBe(false);
    expect(w.find('[data-testid="gantt-span-5"]').exists()).toBe(true);
    const open = w.get('[data-testid="gantt-parent-open-5"]');
    expect(open.classes()).toContain('is-faded');
    await open.trigger('click');
    expect(w.emitted('open')).toEqual([[5]]);
    expect(w.find('[data-testid="gantt-parent-open-10"]').exists()).toBe(false);
    expect(w.get('[data-testid="gantt-parent-title-10"]').element.tagName).toBe('SPAN');
    expect(w.get('[data-testid="gantt-parent-title-10"]').text()).toContain('你沒有權限查看這個父任務');
  });

  it('G54 a visible parent outside the window keeps its own row with an edge arrow', async () => {
    const rows = [cardWire({ id: 6, title: '子一', startDate: '2026-11-21', dueDate: '2026-11-25', level: 'child', parentTaskId: 5 })];
    const parents = [{ id: 5, title: '規劃活動內容', visible: true, row: cardWire({ id: 5, title: '規劃活動內容', startDate: '2026-10-01', dueDate: '2026-10-20', level: 'parent' }) }];
    const w = await mountGantt(() => ganttWire({ rows, parents, spans: [], total: 1 }));
    expect(w.get('[data-testid="gantt-outside-5"]').classes()).toContain('is-before');
    expect(w.get('[data-testid="gantt-outside-5"]').attributes('aria-label')).toBe('規劃活動內容：2026/10/01 – 2026/10/20');
  });

  it('G55 groups by assignee with the inactive mark and unassigned last', async () => {
    const rows = [
      cardWire({ id: 1, title: 'a', assigneeUserId: null, assigneeName: null }),
      cardWire({ id: 2, title: 'b', assigneeUserId: 'old01', assigneeName: '老王', assigneeActive: false }),
      cardWire({ id: 3, title: 'c' }),
    ];
    const w = await mountGantt(() => ganttWire({ rows, config: { version: 1, groupBy: 'assignee', scope: 'assigned', scale: 'week' }, spans: [] }));
    const groups = w.findAll('[data-testid="gantt-group"]').map((g) => g.text());
    expect(groups).toHaveLength(3);
    expect(groups.some((g) => g.includes('老王（已停用）'))).toBe(true);
    expect(groups.at(-1)).toContain('未指派');
  });

  it('G56 lists unscheduled tasks with their start date and the over-100 notice', async () => {
    const w = await mountGantt(() => ganttWire({ unscheduledTotal: 130, unscheduledTruncated: true }));
    expect(w.get('[data-testid="gantt-unscheduled-title"]').text()).toBe('未排程（130）');
    expect(w.get('[data-testid="gantt-unscheduled-start-20"]').text()).toBe('開始日 11/20');
    expect(w.find('[data-testid="gantt-unscheduled-start-21"]').exists()).toBe(false);
    expect(w.get('[data-testid="gantt-unscheduled-more"]').text()).toBe('還有 128 筆，請加上篩選');
    const none = await mountGantt(() => ganttWire({ unscheduled: [], unscheduledTotal: 0 }));
    expect(none.find('[data-testid="gantt-unscheduled"]').exists()).toBe(false);
  });

  it('G57 more than 500 rows shows the notice to filter or shorten the range', async () => {
    const w = await mountGantt(() => ganttWire({ total: 612, truncated: true }));
    expect(w.get('[data-testid="gantt-truncated"]').text()).toBe('只顯示前 5 列（共 612 列），請加上篩選或縮短時間範圍');
    const ok = await mountGantt();
    expect(ok.find('[data-testid="gantt-truncated"]').exists()).toBe(false);
  });

  it('shows the empty text, the failure with retry, and the no-subordinates notice', async () => {
    const empty = await mountGantt(() => ganttWire({ rows: [], spans: [], total: 0, scopeAvailable: false }));
    expect(empty.find('[data-testid="gantt-empty"]').exists()).toBe(true);
    expect(empty.find('[data-testid="gantt-scope-unavailable"]').exists()).toBe(true);
    let fail = true;
    const w = await mountGantt(() => (fail ? new HttpFail(500, { message: 'x' }) : ganttWire()));
    expect(w.find('[data-testid="gantt-load-failed"]').exists()).toBe(true);
    fail = false;
    await w.get('[data-testid="gantt-retry"]').trigger('click');
    await flushPromises();
    expect(w.find('[data-testid="gantt-bar-5"]').exists()).toBe(true);
  });

  it('tablet and phone widths (below 1024) show the notice instead of the chart and send no query', async () => {
    quasar.Screen.lt.md = true;
    const w = await mountGantt();
    expect(w.get('[data-testid="gantt-narrow"]').text()).toContain('甘特圖請用較寬的螢幕，或改用清單檢視');
    expect(w.find('[data-testid="gantt-chart"]').exists()).toBe(false);
    expect(ganttCalls()).toHaveLength(0);
    await w.get('[data-testid="gantt-use-list"]').trigger('click');
    expect(w.emitted('list')).toHaveLength(1);
  });

  it('a touch screen counts as a tablet at any width', async () => {
    vi.stubGlobal('matchMedia', (q: string) => ({ matches: q === '(pointer: coarse)', addEventListener() {}, removeEventListener() {} }));
    const w = await mountGantt();
    expect(w.find('[data-testid="gantt-narrow"]').exists()).toBe(true);
    expect(ganttCalls()).toHaveLength(0);
  });

  it('G61 the scale labels follow the interface language', async () => {
    const w = await mountGantt(serverLike, 'en-US');
    expect(w.get('[data-testid="gantt-scale"]').text()).toContain('Week');
    expect(w.findAll('[data-testid="gantt-major"]').map((e) => e.text())).toEqual(['Nov 2026', 'Dec 2026', 'Jan 2027', 'Feb 2027']);
    await w.get('[data-testid="gantt-scale-day"]').trigger('click');
    await flushPromises();
    expect(w.findAll('[data-testid="gantt-minor"]')[0].text()).toBe('16 Mon');
    expect(w.get('[data-testid="gantt-range"]').text()).toBe('11/16/2026 – 11/29/2026');
  });
});
