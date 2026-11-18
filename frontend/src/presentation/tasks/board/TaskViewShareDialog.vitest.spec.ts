import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import zhTW from 'src/i18n/zh-TW.json';
import { normalizeViewDetail } from 'src/services/api/task-normalize';
import { bodyOf, HttpFail, mockHttp, type Call } from 'src/test-utils/mock-http';
import { shareTargetsWire, viewDetailWire } from './board-fixtures';
import TaskViewShareDialog from './TaskViewShareDialog.vue';

let calls: Call[];
let mounted: ReturnType<typeof mount> | undefined;
let client: QueryClient;
const q = (id: string) => document.body.querySelector<HTMLElement>(`[data-testid="${id}"]`);

async function mountDialog(detail: Record<string, unknown> = {}, opts: { targetsFail?: boolean; putFails?: boolean; put404?: boolean; cached?: Record<string, unknown>; kind?: 'board' | 'gantt' } = {}) {
  calls = mockHttp((c) => {
    if (c.url === '/task-views/share-targets') return opts.targetsFail ? new HttpFail(500, {}) : shareTargetsWire();
    if (c.url === '/task-views/5' && c.method === 'get') return viewDetailWire(detail);
    if (c.url === '/task-views/5/shares') return opts.put404 ? new HttpFail(404, { message: '找不到看板' }) : opts.putFails ? new HttpFail(400, { errors: { shares: ['對象不存在'] } }) : viewDetailWire(detail);
    return [];
  });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  if (opts.cached) queryClient.setQueryData(['task-view', 5], normalizeViewDetail(viewDetailWire(opts.cached) as never));
  client = queryClient;
  mounted = mount(TaskViewShareDialog, {
    props: { viewId: 5, open: true, ...(opts.kind ? { kind: opts.kind } : {}) },
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } }),
      [VueQueryPlugin, { queryClient }]] },
  });
  await flushPromises();
}
const click = async (id: string) => { q(id)!.click(); await flushPromises(); };
const puts = () => calls.filter((c) => c.url === '/task-views/5/shares' && c.method === 'put');

describe('TaskViewShareDialog', () => {
  beforeEach(() => { document.body.innerHTML = ''; vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} }); });
  afterEach(() => { mounted?.unmount(); document.body.innerHTML = ''; vi.unstubAllGlobals(); });

  it('TaskViewShareDialog sends user, department and group targets', async () => {
    await mountDialog({ shares: [{ kind: 'user', id: 'sales03', displayName: '業務三', inactive: false }] });
    expect(q('task-share-user-sales03')!.getAttribute('aria-checked')).toBe('true');
    await click('task-share-user-sales02');
    await click('task-share-tab-department');
    await click('task-share-department-3');
    await click('task-share-tab-group');
    await click('task-share-group-4');
    await click('task-share-save');
    // 部門與群組的 id 是數字，分享的 id 一律是字串
    expect(bodyOf(puts()[0])).toEqual([
      { kind: 'user', id: 'sales03' }, { kind: 'user', id: 'sales02' }, { kind: 'department', id: '3' }, { kind: 'group', id: '4' },
    ]);
  });

  it('TaskViewShareDialog shows the direct-members note on the department tab', async () => {
    await mountDialog();
    expect(q('task-share-dept-note')).toBeNull();
    await click('task-share-tab-department');
    expect(q('task-share-dept-note')!.textContent).toContain('只含直屬成員');
  });

  it('keeps inactive shares that are not in the options and drops ones that no longer exist', async () => {
    await mountDialog({ shares: [
      { kind: 'department', id: '9', displayName: null, nameI18n: { 'zh-TW': '舊部門' }, inactive: true },
      { kind: 'user', id: 'gone01', displayName: null, nameI18n: null, inactive: true },
    ] });
    await click('task-share-tab-department');
    expect(q('task-share-department-9')!.textContent).toContain('舊部門（已停用）');
    await click('task-share-save');
    expect(bodyOf(puts()[0])).toEqual([{ kind: 'department', id: '9' }]);
  });

  it('a save failure shows the localized message, not the server text', async () => {
    await mountDialog({}, { putFails: true });
    await click('task-share-save');
    expect(q('task-share-error')!.textContent).toContain(zhTW.taskBoard.share.saveFailed);
    expect(document.body.textContent).not.toContain('對象不存在');
  });

  it('seeds from the fresh share list, not from an older cached copy (another device may have changed it)', async () => {
    const stale = [{ kind: 'user', id: 'sales03', displayName: '業務三', inactive: false }];
    const fresh = [{ kind: 'user', id: 'sales02', displayName: '業務二', inactive: false }];
    await mountDialog({ shares: fresh }, { cached: { shares: stale } });
    expect(q('task-share-user-sales02')!.getAttribute('aria-checked')).toBe('true');
    expect(q('task-share-user-sales03')!.getAttribute('aria-checked')).toBe('false');
    await click('task-share-save');
    expect(bodyOf(puts()[0])).toEqual([{ kind: 'user', id: 'sales02' }]);
  });

  it('the scroll area does not wrap into columns (no-wrap) and the checkboxes sit in a named group', async () => {
    await mountDialog();
    const body = q('task-share-body')!;
    expect(body.classList.contains('column')).toBe(true);
    expect(body.classList.contains('no-wrap')).toBe(true);
    const group = q('task-share-list')!;
    expect(group.getAttribute('role')).toBe('group');
    expect(group.getAttribute('aria-label')).toBe(zhTW.taskBoard.share.users);
    await click('task-share-tab-department');
    expect(q('task-share-list')!.getAttribute('aria-label')).toBe(zhTW.taskBoard.share.departments);
  });

  it('a 404 on save (the board is gone) shows the failure and refreshes the board list', async () => {
    await mountDialog({}, { put404: true });
    client.setQueryData(['task-views', 'board'], { mine: [], shared: [] });
    await click('task-share-save');
    expect(q('task-share-error')).not.toBeNull();
    expect(client.getQueryState(['task-views', 'board'])?.isInvalidated).toBe(true);
  });

  it('a load failure shows retry and cannot save', async () => {
    await mountDialog({}, { targetsFail: true });
    expect(q('task-share-load-error')).not.toBeNull();
    expect((q('task-share-save') as HTMLButtonElement).disabled).toBe(true);
  });

  it('G74 sharing a Gantt chart uses the same dialog in Gantt words and replaces the shares', async () => {
    await mountDialog({ kind: 'gantt', name: '展覽' }, { kind: 'gantt' });
    expect(q('task-share')!.textContent).toContain('分享甘特圖');
    expect(q('task-share')!.textContent).toContain('分享只讓對方看得到甘特圖設定，任務權限不變');
    await click('task-share-user-sales02');
    await click('task-share-save');
    expect(bodyOf(puts()[0])).toEqual([{ kind: 'user', id: 'sales02' }]);
  });
});
