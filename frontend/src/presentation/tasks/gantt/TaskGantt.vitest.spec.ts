import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import enUS from 'src/i18n/en-US.json';
import zhTW from 'src/i18n/zh-TW.json';
import DateSelect from 'src/components/framework/DateSelect.vue';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { bodyOf, HttpFail, mockHttp, type Call } from 'src/test-utils/mock-http';
import { cardWire } from '../board/board-fixtures';
import { ganttWire } from './gantt-fixtures';
import GanttDateDialog from './GanttDateDialog.vue';
import TaskGantt from './TaskGantt.vue';

let calls: Call[];
const ganttCalls = () => calls.filter((c) => c.url === '/tasks/gantt');
const lastBody = () => bodyOf(ganttCalls().at(-1)) as { viewId: number | null; config: Record<string, unknown> | null; window: { from: string; to: string } | null };

type ScheduleBody = { startDate: string | null; dueDate: string | null; expectedVersion: string };
async function mountGantt(
  reply: (body: ReturnType<typeof lastBody>) => unknown = () => ganttWire(), locale = 'zh-TW',
  schedule: (id: number, body: ScheduleBody) => unknown = () => new HttpFail(500, {}),
) {
  calls = mockHttp((c) => {
    if (c.url === '/tasks/gantt') return reply(bodyOf(c) as ReturnType<typeof lastBody>);
    const m = /^\/tasks\/(\d+)\/schedule$/.exec(c.url);
    if (m) return schedule(Number(m[1]), bodyOf(c) as ScheduleBody);
    return [];
  });
  const i18n = createI18n({ legacy: false, locale, messages: { 'zh-TW': zhTW, 'en-US': enUS } });
  const w = mount(TaskGantt, {
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], i18n, [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false, refetchOnWindowFocus: false } } }) }]] },
  });
  await flushPromises();
  return w;
}

/** 會記住改過的日期的假伺服器：schedule 改了哪一個任務，之後的甘特查詢就照新日期回（沒有到期日的在未排程）。 */
function statefulServer() {
  const base = ganttWire();
  const all = new Map<number, Record<string, unknown>>([...base.rows, ...base.unscheduled].map((r) => [r.id, r]));
  const gantt = () => {
    const list = [...all.values()];
    const rows = list.filter((r) => r.dueDate);
    const unscheduled = list.filter((r) => !r.dueDate);
    return { ...base, rows, unscheduled, total: rows.length, unscheduledTotal: unscheduled.length };
  };
  const schedule = (id: number, b: ScheduleBody) => {
    const card = { ...all.get(id)!, startDate: b.startDate ?? undefined, dueDate: b.dueDate ?? undefined, version: 'v2' };
    all.set(id, card);
    return { card };
  };
  return { gantt, schedule };
}
const scheduleCalls = () => calls.filter((c) => /\/schedule$/.test(c.url));
const dialogOpen = (w: VueWrapper) => w.findComponent(GanttDateDialog).props('modelValue');
const dialogText = () => document.querySelector('[data-testid="gantt-dates"]')?.textContent ?? '';
const dateInputs = (w: VueWrapper) => w.findComponent(GanttDateDialog).findAllComponents(DateSelect);
async function setDates(w: VueWrapper, start: string | null, due: string | null) {
  const [s, d] = dateInputs(w);
  s.vm.$emit('update:modelValue', start);
  d.vm.$emit('update:modelValue', due);
  await flushPromises();
}
const saveButton = () => document.querySelector<HTMLButtonElement>('[data-testid="gantt-dates-save"]')!;
async function save() {
  saveButton().click();
  await flushPromises();
  await new Promise((r) => setTimeout(r));
  await flushPromises();
}
const notifySpy = (w: VueWrapper) => vi.spyOn(w.vm.$q, 'notify').mockImplementation(() => () => {});
const notified = (spy: ReturnType<typeof notifySpy>) => spy.mock.calls.map((c) => (c[0] as { message: string }).message);
const pctOf = (el: { attributes: (n: string) => string | undefined }, prop: 'left' | 'width') => Number(new RegExp(`${prop}: ([\\d.]+)%`).exec(el.attributes('style') ?? '')?.[1]);

/** 依刻度回對應的預設時間窗，像伺服器一樣。 */
function serverLike(body: ReturnType<typeof lastBody>) {
  const scale = (body.config?.scale as string | undefined) ?? 'week';
  const win = body.window ?? (scale === 'day' ? { from: '2026-11-16', to: '2026-11-29' } : scale === 'month' ? { from: '2026-11-01', to: '2027-04-30' } : { from: '2026-11-16', to: '2027-02-14' });
  return ganttWire({ window: win, ...(body.config ? { config: body.config } : {}) });
}

describe('TaskGantt', () => {
  beforeEach(() => { document.body.innerHTML = ''; });
  afterEach(() => { quasar.Screen.lt.md = false; quasar.Screen.lt.sm = false; vi.unstubAllGlobals(); vi.restoreAllMocks(); document.body.innerHTML = ''; });

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

  it('pressing today on the default window asks the server again (a new day after midnight)', async () => {
    let today = '2026-11-22';
    const w = await mountGantt(() => ganttWire({ today }));
    expect(ganttCalls()).toHaveLength(1);
    today = '2026-11-23';
    await w.get('[data-testid="gantt-today"]').trigger('click');
    await flushPromises();
    expect(ganttCalls()).toHaveLength(2);
    expect(lastBody()).toEqual({ viewId: null, config: null, window: null });
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

  it('G58 tablet (below 1024): left column 200px; clicking a bar opens the dates dialog, saving sends schedule with the version and the bar moves', async () => {
    quasar.Screen.lt.md = true;
    const srv = statefulServer();
    const w = await mountGantt(srv.gantt, 'zh-TW', srv.schedule);
    expect(w.get('[data-testid="gantt-chart"]').attributes('style')).toContain('--gantt-left: 200px');
    await w.get('[data-testid="gantt-bar-7"]').trigger('click');
    await flushPromises();
    expect(w.emitted('open')).toBeUndefined();
    expect(dialogOpen(w)).toBe(true);
    expect(document.querySelector('[data-testid="gantt-dates-task"]')!.textContent).toContain('整理邀請客戶名單');
    expect(dateInputs(w).map((d) => d.props('modelValue'))).toEqual(['2026-11-21', '2026-12-02']);
    await setDates(w, '2026-11-30', '2026-12-10');
    await save();
    expect(scheduleCalls().map((c) => c.url)).toEqual(['/tasks/7/schedule']);
    expect(bodyOf(scheduleCalls()[0])).toEqual({ startDate: '2026-11-30', dueDate: '2026-12-10', expectedVersion: '20261101020000000' });
    expect(pctOf(w.get('[data-testid="gantt-bar-7"]'), 'left')).toBeCloseTo((14 / 91) * 100, 3);
    expect(pctOf(w.get('[data-testid="gantt-bar-7"]'), 'width')).toBeCloseTo((11 / 91) * 100, 3);
    expect(dialogOpen(w)).toBe(false);
  });

  it('G58 a touch screen counts as a tablet at any width', async () => {
    vi.stubGlobal('matchMedia', (q: string) => ({ matches: q === '(pointer: coarse)', addEventListener() {}, removeEventListener() {} }));
    const w = await mountGantt();
    expect(w.get('[data-testid="gantt-chart"]').attributes('style')).toContain('--gantt-left: 200px');
    await w.get('[data-testid="gantt-milestone-8"]').trigger('click');
    await flushPromises();
    expect(w.emitted('open')).toBeUndefined();
    expect(dialogOpen(w)).toBe(true);
  });

  it('G58 a stale version (409) tells the user, closes the dialog, puts the bar back and reloads the chart', async () => {
    quasar.Screen.lt.md = true;
    const w = await mountGantt(() => ganttWire(), 'zh-TW', () => new HttpFail(409, { code: 'stale', message: 'SERVER-TEXT' }));
    const spy = notifySpy(w);
    const before = ganttCalls().length;
    await w.get('[data-testid="gantt-bar-7"]').trigger('click');
    await flushPromises();
    await setDates(w, '2026-11-30', '2026-12-10');
    await save();
    expect(notified(spy)).toEqual(['任務已被別人修改，已重新整理']);
    expect(ganttCalls().length).toBeGreaterThan(before);
    expect(pctOf(w.get('[data-testid="gantt-bar-7"]'), 'left')).toBeCloseTo((5 / 91) * 100, 3);
    expect(dialogOpen(w)).toBe(false);
  });

  it('G58 tasks whose dates cannot be changed (no permission, frozen) have no edit or schedule button and their bar opens the task', async () => {
    quasar.Screen.lt.md = true;
    const rows = [
      cardWire({ id: 31, title: '部屬的任務', startDate: '2026-11-17', dueDate: '2026-11-20', canEdit: false }),
      cardWire({ id: 32, title: '凍結的任務', startDate: '2026-11-17', dueDate: '2026-11-20', frozen: true }),
    ];
    const w = await mountGantt(() => ganttWire({ rows, spans: [], total: 2, unscheduled: [cardWire({ id: 33, dueDate: undefined, canEdit: false })], unscheduledTotal: 1 }));
    expect(w.find('[data-testid="gantt-edit-31"]').exists()).toBe(false);
    expect(w.find('[data-testid="gantt-edit-32"]').exists()).toBe(false);
    expect(w.find('[data-testid="gantt-schedule-33"]').exists()).toBe(false);
    await w.get('[data-testid="gantt-bar-31"]').trigger('click');
    await w.get('[data-testid="gantt-bar-32"]').trigger('click');
    expect(w.emitted('open')).toEqual([[31], [32]]);
    expect(dialogOpen(w)).toBe(false);
  });

  it('G58 the dialog blocks a start after the due date at once and puts a server 400 under the field without closing', async () => {
    const w = await mountGantt(() => ganttWire(), 'zh-TW', () => new HttpFail(400, { errors: { startDate: ['SERVER'] } }));
    const spy = notifySpy(w);
    await w.get('[data-testid="gantt-edit-7"]').trigger('click');
    await flushPromises();
    await setDates(w, '2026-12-05', '2026-12-02');
    expect(dialogText()).toContain('開始日不能晚於到期日');
    expect(saveButton().classList.contains('disabled')).toBe(true);
    await save();
    expect(scheduleCalls()).toHaveLength(0);
    await setDates(w, '2026-11-25', '2026-12-02');
    expect(dialogText()).not.toContain('開始日不能晚於到期日');
    await save();
    expect(scheduleCalls()).toHaveLength(1);
    expect(dialogText()).toContain('開始日不能晚於到期日');
    expect(dialogText()).not.toContain('SERVER');
    expect(notified(spy)).toEqual([]);
    expect(dialogOpen(w)).toBe(true);
  });

  it('G58 clearing both dates moves the task to Unscheduled', async () => {
    const srv = statefulServer();
    const w = await mountGantt(srv.gantt, 'zh-TW', srv.schedule);
    await w.get('[data-testid="gantt-edit-8"]').trigger('click');
    await flushPromises();
    await setDates(w, null, null);
    await save();
    expect(bodyOf(scheduleCalls()[0])).toEqual({ startDate: null, dueDate: null, expectedVersion: '20261101020000000' });
    expect(w.find('[data-testid="gantt-milestone-8"]').exists()).toBe(false);
    expect(w.find('[data-testid="gantt-unscheduled-8"]').exists()).toBe(true);
  });

  it('G60 every row has a Tab-reachable edit-dates button and unscheduled rows a schedule button; on a desktop the bar still opens the task', async () => {
    const srv = statefulServer();
    const w = await mountGantt(srv.gantt, 'zh-TW', srv.schedule);
    for (const id of [5, 6, 7, 8, 9]) {
      const edit = w.get(`[data-testid="gantt-edit-${id}"]`);
      expect(edit.element.tagName).toBe('BUTTON');
      expect(edit.attributes('tabindex')).not.toBe('-1');
    }
    expect(w.get('[data-testid="gantt-edit-7"]').attributes('aria-label')).toBe('編輯「整理邀請客戶名單」的日期');
    expect(w.get('[data-testid="gantt-chart"]').attributes('style')).toContain('--gantt-left: 280px');
    await w.get('[data-testid="gantt-bar-7"]').trigger('click');
    expect(w.emitted('open')).toEqual([[7]]);
    expect(dialogOpen(w)).toBe(false);
    // Enter on a native button is a click
    await w.get('[data-testid="gantt-edit-7"]').trigger('click');
    await flushPromises();
    expect(dialogOpen(w)).toBe(true);
    w.findComponent(GanttDateDialog).vm.$emit('update:modelValue', false);
    await flushPromises();

    const sched = w.get('[data-testid="gantt-schedule-21"]');
    expect(sched.element.tagName).toBe('BUTTON');
    expect(sched.attributes('aria-label')).toBe('排程「想想看」');
    await sched.trigger('click');
    await flushPromises();
    expect(w.emitted('open')).toEqual([[7]]);
    expect(dateInputs(w).map((d) => d.props('modelValue'))).toEqual([null, null]);
    await setDates(w, '2026-11-25', '2026-11-25');
    await save();
    expect(bodyOf(scheduleCalls()[0])).toEqual({ startDate: '2026-11-25', dueDate: '2026-11-25', expectedVersion: '20261101020000000' });
    expect(w.find('[data-testid="gantt-unscheduled-21"]').exists()).toBe(false);
    expect(w.find('[data-testid="gantt-bar-21"]').exists()).toBe(true);
  });

  it('G59 phone: no timeline; four weeks from this week; a task over three weeks only in its start week; an earlier start goes to the first week', async () => {
    quasar.Screen.lt.sm = true;
    quasar.Screen.lt.md = true;
    const rows = [
      cardWire({ id: 5, title: '規劃活動內容', status: 'inProgress', startDate: '2026-11-16', dueDate: '2026-12-01', level: 'parent', progress: { done: 1, cancelled: 0, total: 3 } }),
      cardWire({ id: 7, title: '整理邀請客戶名單', startDate: '2026-11-23', dueDate: '2026-12-09', level: 'child', parentTaskId: 5, parentTitle: '規劃活動內容' }),
      cardWire({ id: 8, title: '寄合約', dueDate: '2026-12-01' }),
      cardWire({ id: 9, title: '回覆報價', startDate: '2026-11-10', dueDate: '2026-11-17', overdue: true }),
    ];
    const w = await mountGantt((b) => ganttWire({ rows, spans: [], total: 4, window: b.window ?? { from: '2026-11-16', to: '2027-02-14' } }));
    expect(bodyOf(ganttCalls()[0])).toEqual({ viewId: null, config: null, window: null });
    expect(lastBody().window).toEqual({ from: '2026-11-16', to: '2026-12-13' });
    expect(w.find('[data-testid="gantt-chart"]').exists()).toBe(false);
    expect(w.find('[data-testid="gantt-scale"]').exists()).toBe(false);
    expect(w.findAll('[data-testid="gantt-week-title"]').map((e) => e.text())).toEqual(['11/16 – 11/22', '11/23 – 11/29', '11/30 – 12/6', '12/7 – 12/13']);
    const weekIds = (from: string) => w.get(`[data-testid="gantt-week-${from}"]`).findAll('[data-testid^="gantt-week-item-"]').map((e) => e.attributes('data-testid'));
    expect(weekIds('2026-11-16')).toEqual(['gantt-week-item-5', 'gantt-week-item-9']);
    expect(weekIds('2026-11-23')).toEqual(['gantt-week-item-7']);
    expect(weekIds('2026-11-30')).toEqual(['gantt-week-item-8']);
    expect(w.get('[data-testid="gantt-week-2026-12-07"]').find('[data-testid="gantt-week-empty"]').exists()).toBe(true);
    expect(w.findAll('[data-testid="gantt-week-item-7"]')).toHaveLength(1);
    expect(w.get('[data-testid="gantt-week-parent-7"]').text()).toContain('規劃活動內容');
    expect(w.get('[data-testid="gantt-week-dates-7"]').text()).toContain('11/23 → 12/9');
    expect(w.get('[data-testid="gantt-week-dates-8"]').text()).toContain('◆ 12/1');
    expect(w.get('[data-testid="gantt-week-dates-9"]').classes()).toContain('text-negative');
    expect(w.get('[data-testid="gantt-week-item-5"]').find('[data-testid="subtask-progress-text"]').exists()).toBe(true);
    expect(w.find('[data-testid^="gantt-edit-"]').exists()).toBe(false);

    await w.get('[data-testid="gantt-week-item-7"]').trigger('click');
    expect(w.emitted('open')).toEqual([[7]]);

    await w.get('[data-testid="gantt-next"]').trigger('click');
    await flushPromises();
    expect(lastBody().window).toEqual({ from: '2026-12-14', to: '2027-01-10' });
    expect(w.findAll('[data-testid="gantt-week-title"]')[0].text()).toBe('12/14 – 12/20');
    await w.get('[data-testid="gantt-prev"]').trigger('click');
    await flushPromises();
    expect(lastBody().window).toEqual({ from: '2026-11-16', to: '2026-12-13' });

    const unscheduled = w.get('[data-testid="gantt-unscheduled"]');
    expect(unscheduled.text()).toContain('未排程（2）');
    await unscheduled.get('.q-item').trigger('click');
    await flushPromises();
    await w.get('[data-testid="gantt-unscheduled-20"]').trigger('click');
    expect(w.emitted('open')).toEqual([[7], [20]]);
  });

  it('G59 phone: pressing today after midnight into a new week asks the server for today first, then loads that week', async () => {
    quasar.Screen.lt.sm = true;
    quasar.Screen.lt.md = true;
    let today = '2026-11-22';
    const w = await mountGantt((b) => ganttWire({ today, window: b.window ?? { from: '2026-11-16', to: '2027-02-14' } }));
    expect(lastBody().window).toEqual({ from: '2026-11-16', to: '2026-12-13' });
    today = '2026-11-23';
    await w.get('[data-testid="gantt-today"]').trigger('click');
    await flushPromises();
    expect(bodyOf(ganttCalls().at(-2))).toEqual({ viewId: null, config: null, window: null });
    expect(lastBody().window).toEqual({ from: '2026-11-23', to: '2026-12-20' });
    expect(w.findAll('[data-testid="gantt-week-title"]')[0].text()).toBe('11/23 – 11/29');
    // 同一週再按：照樣問一次今天，再重抓同一段
    const before = ganttCalls().length;
    await w.get('[data-testid="gantt-today"]').trigger('click');
    await flushPromises();
    expect(ganttCalls().length).toBe(before + 2);
    expect(lastBody().window).toEqual({ from: '2026-11-23', to: '2026-12-20' });
  });

  it('G59 phone: a failed next period keeps the old weeks, shows retry and leaves the navigation usable', async () => {
    quasar.Screen.lt.sm = true;
    quasar.Screen.lt.md = true;
    let fail = false;
    const w = await mountGantt((b) => (fail ? new HttpFail(500, {}) : ganttWire({ window: b.window ?? { from: '2026-11-16', to: '2027-02-14' } })));
    fail = true;
    await w.get('[data-testid="gantt-next"]').trigger('click');
    await flushPromises();
    expect(w.find('[data-testid="gantt-load-failed"]').exists()).toBe(true);
    expect(w.findAll('[data-testid="gantt-week-title"]')[0].text()).toBe('11/16 – 11/22');
    expect(w.get('[data-testid="gantt-next"]').classes()).not.toContain('disabled');
    expect(w.get('[data-testid="gantt-prev"]').classes()).not.toContain('disabled');
    fail = false;
    await w.get('[data-testid="gantt-retry"]').trigger('click');
    await flushPromises();
    expect(w.find('[data-testid="gantt-load-failed"]').exists()).toBe(false);
    expect(w.findAll('[data-testid="gantt-week-title"]')[0].text()).toBe('12/14 – 12/20');
  });

  it('G58 an answer for a dialog that was closed does not close or mark the next one', async () => {
    const fails: ((e: unknown) => void)[] = [];
    const w = await mountGantt();
    vi.spyOn(dynamicApi, 'scheduleTask').mockImplementation(() => new Promise((_ok, no) => { fails.push(no); }));
    const spy = notifySpy(w);
    await w.get('[data-testid="gantt-edit-7"]').trigger('click');
    await flushPromises();
    await setDates(w, '2026-11-25', '2026-12-02');
    saveButton().click();
    await flushPromises();
    // Esc 關掉 A，再開 B
    w.findComponent(GanttDateDialog).vm.$emit('update:modelValue', false);
    await flushPromises();
    await w.get('[data-testid="gantt-edit-8"]').trigger('click');
    await flushPromises();
    expect(dialogOpen(w)).toBe(true);
    expect(saveButton().classList.contains('disabled')).toBe(true);
    fails[0]({ status: 400, errors: { startDate: ['SERVER'] } });
    await flushPromises();
    expect(dialogOpen(w)).toBe(true);
    expect(dialogText()).not.toContain('開始日不能晚於到期日');
    expect(dateInputs(w).map((d) => d.props('modelValue'))).toEqual([null, '2026-11-20']);
    await setDates(w, '2026-11-18', '2026-11-20');
    expect(saveButton().classList.contains('disabled')).toBe(false);
    expect(notified(spy)).toEqual([]);
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
