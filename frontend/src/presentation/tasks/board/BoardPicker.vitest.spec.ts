import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { TaskBoardConfig, TaskViewConfig, TaskViewKind, TaskViewList } from 'src/domain/dynamic/task-board-types';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import { normalizeViewList } from 'src/services/api/task-normalize';
import { bodyOf, HttpFail, mockHttp, type Call } from 'src/test-utils/mock-http';
import { ganttConfigWire } from '../gantt/gantt-fixtures';
import { configWire, viewDetailWire, viewSummaryWire } from './board-fixtures';
import BoardPicker from './BoardPicker.vue';

let calls: Call[];
let mounted: ReturnType<typeof mount> | undefined;
const q = (id: string) => document.body.querySelector<HTMLElement>(`[data-testid="${id}"]`);
const views = (): TaskViewList => normalizeViewList({
  mine: [viewSummaryWire({ id: 5, name: '我的到期' }), viewSummaryWire({ id: 6, name: '業務部負載', shareCount: 1 })],
  shared: [viewSummaryWire({ id: 8, name: '業務部任務', isOwner: false, ownerUserId: 'manager01', ownerName: '業務主管', shareCount: 1 })],
});
const config = configWire({ groupBy: 'dueBucket' }) as unknown as TaskBoardConfig;

async function mountPicker(o: Partial<{ deleteFails: number; isAdmin: boolean; hasSubordinates: boolean; currentId: number | null; currentName: string; unsaved: boolean; config: TaskViewConfig; kind: TaskViewKind; views: TaskViewList }> = {}) {
  calls = mockHttp((c) => {
    if (c.method === 'delete' && o.deleteFails) return new HttpFail(o.deleteFails, { message: '伺服器訊息' });
    if (c.url === '/task-views' && c.method === 'post') return viewDetailWire({ id: 11, name: (bodyOf(c) as { name: string }).name });
    return [];
  });
  mounted = mount(BoardPicker, {
    props: { views: views(), currentId: null, currentName: '預設看板', unsaved: false, isAdmin: false, hasSubordinates: true, config, ...o },
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } }),
      [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]] },
  });
  await flushPromises();
  q('board-picker')!.click();
  await flushPromises();
  return mounted;
}

describe('BoardPicker', () => {
  beforeEach(() => { document.body.innerHTML = ''; });
  afterEach(() => { mounted?.unmount(); document.body.innerHTML = ''; });

  it('BoardPicker groups mine and shared, shows the owner', async () => {
    await mountPicker();
    const text = (id: string) => q(id)!.textContent ?? '';
    expect(text('picker-group-mine')).toContain('我的看板');
    expect(text('picker-group-shared')).toContain('分享給我的');
    expect(q('picker-item-default')).not.toBeNull();
    expect(q('picker-group-mine')!.parentElement!.querySelector('[data-testid="picker-item-5"]')).not.toBeNull();
    expect(text('picker-item-8')).toContain('業務部任務');
    expect(text('picker-item-8')).toContain('業務主管 建立');
    // 自己的看板不帶建立者
    expect(text('picker-item-5')).not.toContain('建立');
  });

  it('selecting a board emits its id', async () => {
    const w = await mountPicker({ currentId: 5 });
    q('picker-item-8')!.click();
    await flushPromises();
    expect(w.emitted('select')).toEqual([[8]]);
  });

  it('selecting the default board emits null', async () => {
    const w = await mountPicker({ currentId: 5 });
    q('picker-item-default')!.click();
    await flushPromises();
    expect(w.emitted('select')).toEqual([[null]]);
  });

  it("BoardPicker admin sees delete only on others' shared boards", async () => {
    await mountPicker({ isAdmin: true });
    // 管理員在「分享給我的」只有刪除，沒有分享；自己的看板分享與刪除都有
    expect(q('picker-delete-8')).not.toBeNull();
    expect(q('picker-share-8')).toBeNull();
    expect(q('picker-share-5')).not.toBeNull();
    expect(q('picker-delete-5')).not.toBeNull();
  });

  it('a normal user has no delete on shared boards', async () => {
    await mountPicker({ isAdmin: false });
    expect(q('picker-delete-8')).toBeNull();
    expect(q('picker-share-8')).toBeNull();
    expect(q('picker-delete-5')).not.toBeNull();
  });

  it('delete asks first, then deletes and emits deleted', async () => {
    calls = [];
    const w = await mountPicker({ isAdmin: true });
    q('picker-delete-8')!.click();
    await flushPromises();
    expect(q('picker-delete-confirm')!.textContent).toContain('業務主管');
    expect(calls.some((c) => c.method === 'delete')).toBe(false);
    q('picker-delete-ok')!.click();
    await flushPromises();
    expect(calls.find((c) => c.method === 'delete')!.url).toBe('/task-views/8');
    expect(w.emitted('deleted')).toEqual([[8]]);
  });

  it('BoardPicker save as new board posts the current config', async () => {
    const w = await mountPicker({ currentId: 5 });
    q('picker-save-as')!.click();
    await flushPromises();
    const input = q('picker-name') as HTMLInputElement;
    input.value = '我的到期（新）';
    input.dispatchEvent(new Event('input'));
    await flushPromises();
    q('picker-name-ok')!.click();
    await flushPromises();
    const post = calls.find((c) => c.method === 'post' && c.url === '/task-views')!;
    expect(bodyOf(post)).toEqual({ kind: 'board', name: '我的到期（新）', config });
    expect(w.emitted('created')![0][0]).toMatchObject({ id: 11 });
  });

  it('save as new refuses an empty name', async () => {
    await mountPicker();
    q('picker-save-as')!.click();
    await flushPromises();
    q('picker-name-ok')!.click();
    await flushPromises();
    expect(calls.some((c) => c.method === 'post')).toBe(false);
    expect(q('picker-save-as-dialog')!.textContent).toContain('請輸入看板名稱');
  });

  it('without subordinates a subordinates scope is saved as assigned and the user is told', async () => {
    await mountPicker({ hasSubordinates: false, config: configWire({ groupBy: 'assignee', scope: 'subordinates' }) as unknown as TaskBoardConfig });
    q('picker-save-as')!.click();
    await flushPromises();
    expect(q('picker-scope-swapped')).not.toBeNull();
    const input = q('picker-name') as HTMLInputElement;
    input.value = 'X';
    input.dispatchEvent(new Event('input'));
    await flushPromises();
    q('picker-name-ok')!.click();
    await flushPromises();
    expect((bodyOf(calls.find((c) => c.method === 'post')) as { config: TaskBoardConfig }).config.scope).toBe('assigned');
  });

  it('shows (unsaved) next to the current board name', async () => {
    await mountPicker({ currentId: 5, currentName: '我的到期', unsaved: true });
    expect(q('board-picker')!.textContent).toContain('我的到期');
    expect(q('board-picker')!.textContent).toContain('（未儲存）');
  });

  it('the button has no aria-label that would hide the board name and the unsaved mark from a screen reader', async () => {
    await mountPicker({ currentId: 5, currentName: '我的到期', unsaved: true });
    const button = q('board-picker')!;
    expect(button.hasAttribute('aria-label')).toBe(false);
    expect(button.getAttribute('aria-haspopup')).toBe('menu');
    // 可及名稱來自按鈕內文：看板名稱與「（未儲存）」都在裡面
    expect(button.textContent).toContain('我的到期');
    expect(button.textContent).toContain('（未儲存）');
  });

  it('deleting a board somebody else already deleted (404) counts as deleted', async () => {
    const w = await mountPicker({ isAdmin: true, deleteFails: 404 });
    q('picker-delete-8')!.click();
    await flushPromises();
    q('picker-delete-ok')!.click();
    await flushPromises();
    expect(w.emitted('deleted')).toEqual([[8]]);
    expect(q('picker-delete-error')).toBeNull();
  });

  it('another delete failure keeps the dialog open with the localized message', async () => {
    const w = await mountPicker({ isAdmin: true, deleteFails: 500 });
    q('picker-delete-8')!.click();
    await flushPromises();
    q('picker-delete-ok')!.click();
    await flushPromises();
    expect(w.emitted('deleted')).toBeUndefined();
    expect(q('picker-delete-error')!.textContent).toContain(zhTW.taskBoard.picker.deleteFailed);
  });

  it('closing the delete dialog puts the focus back on the picker button (the menu item that opened it is gone)', async () => {
    await mountPicker({ isAdmin: true });
    q('picker-delete-8')!.click();
    await flushPromises();
    await new Promise((r) => setTimeout(r, 400));
    q('picker-delete-cancel')!.click();
    await new Promise((r) => setTimeout(r, 600));
    await flushPromises();
    expect(document.activeElement).toBe(q('board-picker'));
  });

  it('G72 the Gantt chart picker lists the default, mine, shared and save-as-new in Gantt words, and save-as creates a Gantt view', async () => {
    const gantt = ganttConfigWire() as unknown as TaskViewConfig;
    const list = normalizeViewList({ mine: [viewSummaryWire({ id: 12, kind: 'gantt', name: '展覽' })], shared: [viewSummaryWire({ id: 13, kind: 'gantt', name: '業務部甘特', isOwner: false, ownerName: '業務主管', shareCount: 1 })] });
    await mountPicker({ kind: 'gantt', views: list, config: gantt, currentName: '預設甘特圖' });
    const text = (id: string) => q(id)!.textContent ?? '';
    expect(text('picker-item-default')).toContain('預設甘特圖');
    expect(text('picker-group-mine')).toContain('我的甘特圖');
    expect(text('picker-item-13')).toContain('業務主管 建立');
    expect(text('picker-save-as')).toContain('另存為新甘特圖');
    expect(q('picker-share-12')!.getAttribute('aria-label')).toBe('分享甘特圖「展覽」');
    q('picker-save-as')!.click();
    await flushPromises();
    const input = q('picker-name') as HTMLInputElement;
    input.value = '我的甘特';
    input.dispatchEvent(new Event('input'));
    await flushPromises();
    q('picker-name-ok')!.click();
    await flushPromises();
    expect(bodyOf(calls.find((c) => c.method === 'post' && c.url === '/task-views'))).toEqual({ kind: 'gantt', name: '我的甘特', config: gantt });
  });
});
