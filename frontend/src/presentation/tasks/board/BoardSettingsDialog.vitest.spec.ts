import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { TaskBoardConfig } from 'src/domain/dynamic/task-board-types';
import zhTW from 'src/i18n/zh-TW.json';
import { USERS } from 'src/presentation/dynamic/tasks/task-fixtures';
import { bodyOf, HttpFail, mockHttp, type Call } from 'src/test-utils/mock-http';
import { configWire, viewDetailWire } from './board-fixtures';
import BoardSettingsDialog from './BoardSettingsDialog.vue';

let calls: Call[];
let mounted: ReturnType<typeof mount> | undefined;
const q = (id: string) => document.body.querySelector<HTMLElement>(`[data-testid="${id}"]`);
const click = async (id: string) => { q(id)!.click(); await flushPromises(); };
const config = (o: Record<string, unknown> = {}) => configWire(o) as unknown as TaskBoardConfig;

async function mountDialog(o: Partial<{ config: TaskBoardConfig; canSave: boolean; hasSubordinates: boolean; viewId: number | null; saveFails: number }> = {}) {
  calls = mockHttp((c) => {
    if (c.url === '/users') return USERS;
    if (c.url === '/task-views/5' && c.method === 'put') return o.saveFails ? new HttpFail(o.saveFails, { message: '伺服器訊息', code: 'stale' }) : viewDetailWire({ ...(bodyOf(c) as object), rowVersion: 'v2' });
    if (c.url === '/task-views' && c.method === 'post') return viewDetailWire({ id: 12, name: (bodyOf(c) as { name: string }).name });
    return [];
  });
  mounted = mount(BoardSettingsDialog, {
    props: { modelValue: true, config: config(), name: '我的到期', viewId: 5, rowVersion: 'v1', canSave: true, hasSubordinates: true, ...o },
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } }),
      [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]] },
  });
  await flushPromises();
  return mounted;
}
const writes = () => calls.filter((c) => c.method === 'put' || c.method === 'post');

describe('BoardSettingsDialog', () => {
  beforeEach(() => { document.body.innerHTML = ''; vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} }); });
  afterEach(() => { mounted?.unmount(); document.body.innerHTML = ''; vi.unstubAllGlobals(); });

  it('BoardSettingsDialog hides the subordinates scope without capability', async () => {
    await mountDialog({ hasSubordinates: false });
    expect(q('settings-scope-assigned')).not.toBeNull();
    expect(q('settings-scope-visible')).not.toBeNull();
    expect(q('settings-scope-subordinates')).toBeNull();
    mounted!.unmount();
    document.body.innerHTML = '';
    await mountDialog({ hasSubordinates: true });
    expect(q('settings-scope-subordinates')).not.toBeNull();
  });

  it('a subordinates scope without the capability opens as assigned, with a note (D7)', async () => {
    const w = await mountDialog({ hasSubordinates: false, config: config({ groupBy: 'assignee', scope: 'subordinates' }) });
    expect(q('settings-scope-swapped')).not.toBeNull();
    await click('settings-apply');
    expect((w.emitted('apply')![0][0] as TaskBoardConfig).scope).toBe('assigned');
  });

  it('BoardSettingsDialog apply does not save', async () => {
    const w = await mountDialog();
    await click('settings-group-dueBucket');
    await click('settings-apply');
    expect(writes()).toEqual([]);
    expect(w.emitted('apply')![0][0]).toMatchObject({ version: 1, groupBy: 'dueBucket', scope: 'assigned' });
    expect(w.emitted('update:modelValue')!.at(-1)).toEqual([false]);
  });

  it('BoardSettingsDialog save sends rowVersion and marks clean', async () => {
    const w = await mountDialog();
    await click('settings-scope-visible');
    await click('settings-save');
    const put = calls.find((c) => c.method === 'put')!;
    expect(put.url).toBe('/task-views/5');
    expect(bodyOf(put)).toEqual({ name: '我的到期', config: expect.objectContaining({ groupBy: 'status', scope: 'visible' }), rowVersion: 'v1' });
    // 「已儲存」：發 saved（頁面清掉未儲存標記），不發 apply
    expect(w.emitted('saved')![0][0]).toMatchObject({ id: 5, rowVersion: 'v2' });
    expect(w.emitted('apply')).toBeUndefined();
    expect(w.emitted('update:modelValue')!.at(-1)).toEqual([false]);
  });

  it('save sends the edited name', async () => {
    await mountDialog();
    const input = q('settings-name') as HTMLInputElement;
    input.value = '  我的本週  ';
    input.dispatchEvent(new Event('input'));
    await flushPromises();
    await click('settings-save');
    expect((bodyOf(calls.find((c) => c.method === 'put')) as { name: string }).name).toBe('我的本週');
  });

  it('a stale save shows the localized message and keeps the dialog open', async () => {
    const w = await mountDialog({ saveFails: 409 });
    await click('settings-save');
    expect(q('settings-error')!.textContent).toContain(zhTW.taskBoard.settings.stale);
    expect(document.body.textContent).not.toContain('伺服器訊息');
    expect(w.emitted('saved')).toBeUndefined();
  });

  it('BoardSettingsDialog shared board offers only apply and save-as', async () => {
    await mountDialog({ canSave: false, viewId: 8 });
    expect(q('settings-apply')).not.toBeNull();
    expect(q('settings-save-as')).not.toBeNull();
    expect(q('settings-save')).toBeNull();
    expect(q('settings-readonly-note')).not.toBeNull();
  });

  it('save as new board posts the edited config under the typed name and emits savedAsNew', async () => {
    const w = await mountDialog({ canSave: false, viewId: null });
    const input = q('settings-name') as HTMLInputElement;
    input.value = '我的新看板';
    input.dispatchEvent(new Event('input'));
    await flushPromises();
    await click('settings-group-dueBucket');
    await click('settings-save-as');
    const post = calls.find((c) => c.method === 'post')!;
    expect(bodyOf(post)).toEqual({ kind: 'board', name: '我的新看板', config: expect.objectContaining({ groupBy: 'dueBucket' }) });
    expect(w.emitted('savedAsNew')![0][0]).toMatchObject({ id: 12 });
  });

  it('BoardSettingsDialog moves columns up and down', async () => {
    const w = await mountDialog();
    const order = () => [...document.body.querySelectorAll('[data-testid^="settings-col-row-"]')].map((e) => e.getAttribute('data-testid')!.replace('settings-col-row-', ''));
    expect(order()).toEqual(['todo', 'inProgress', 'done', 'cancelled']);
    await click('settings-col-down-todo');
    expect(order()).toEqual(['inProgress', 'todo', 'done', 'cancelled']);
    await click('settings-col-up-done');
    expect(order()).toEqual(['inProgress', 'done', 'todo', 'cancelled']);
    // 頭尾的按鈕停用
    expect((q('settings-col-up-inProgress') as HTMLButtonElement).disabled).toBe(true);
    expect((q('settings-col-down-cancelled') as HTMLButtonElement).disabled).toBe(true);
    await click('settings-apply');
    expect((w.emitted('apply')![0][0] as TaskBoardConfig).columns).toEqual({ order: ['inProgress', 'done', 'todo', 'cancelled'], hidden: ['cancelled'] });
  });

  it('hiding and showing a column goes into the config', async () => {
    const w = await mountDialog();
    await click('settings-col-show-cancelled');
    await click('settings-col-show-done');
    await click('settings-apply');
    expect((w.emitted('apply')![0][0] as TaskBoardConfig).columns).toEqual({ hidden: ['done'] });
  });

  it('an assignee board lists explicit assignees (move, remove) and the auto-append switch', async () => {
    const w = await mountDialog({ config: config({ groupBy: 'assignee', scope: 'subordinates', columns: { assignees: ['sales01', 'sales02'] } }) });
    expect(q('settings-col-row-todo')).toBeNull();
    expect(q('settings-col-row-unassigned')).not.toBeNull();
    await click('settings-assignee-down-sales01');
    await click('settings-auto-append');
    await click('settings-apply');
    expect((w.emitted('apply')![0][0] as TaskBoardConfig).columns).toEqual({ assignees: ['sales02', 'sales01'], autoAppendAssignees: false });
    // 換分欄依據後，欄順序與隱藏重來
    await click('settings-group-status');
    expect([...document.body.querySelectorAll('[data-testid^="settings-col-row-"]')].length).toBe(4);
  });

  it('switching the group-by resets the column order', async () => {
    const w = await mountDialog();
    await click('settings-col-down-todo');
    await click('settings-group-dueBucket');
    await click('settings-apply');
    expect((w.emitted('apply')![0][0] as TaskBoardConfig).columns).toBeUndefined();
  });

  it('status and source filters, keyword and sort go into the config', async () => {
    const w = await mountDialog();
    await click('settings-status-todo');
    await click('settings-source-rule');
    const kw = q('settings-keyword') as HTMLInputElement;
    kw.value = '報價';
    kw.dispatchEvent(new Event('input'));
    await flushPromises();
    await click('settings-sort-dir-desc');
    await click('settings-apply');
    expect(w.emitted('apply')![0][0]).toMatchObject({ filters: { statuses: ['todo'], sources: ['rule'], keyword: '報價' }, sort: { key: 'dueDate', dir: 'desc' } });
  });

  it('is maximized on a phone and offers the name field only when it can be saved', async () => {
    await mountDialog({ canSave: false, viewId: null });
    expect(q('settings-name')).not.toBeNull();
  });
});
