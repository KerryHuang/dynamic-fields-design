import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { ApprovalActionView, ApprovalStageView, ApprovalTaskView } from 'src/domain/dynamic/approval-types';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import ProgressNodeDetail from './ProgressNodeDetail.vue';

const task = (p: Partial<ApprovalTaskView> = {}): ApprovalTaskView => ({
  id: 1, assigneeUserId: 'u1', assigneeName: '王小華', originalUserId: 'u1', reason: 'resolved', status: 'open', actedTime: null, canAct: false, canReturnPrevious: false, ...p,
});
const stage = (id: number, seq: number, p: Partial<ApprovalStageView> = {}): ApprovalStageView => ({
  id, seq, stageCode: 's', name: { 'zh-TW': '財務二' }, mode: 'all', status: 'active', skipReason: null, dueTime: null, tasks: [task({ id })], editableFieldKeys: [], ...p,
});
const action = (stageRunId: number | null, comment: string, p: Partial<ApprovalActionView> = {}): ApprovalActionView => ({
  action: 'reject', actorUserId: 'u1', actorName: '王小華', onBehalfOfUserId: null, targetUserIds: [], comment, data: null, createdTime: '2026-11-15T10:32:00', stageRunId, ...p,
});

let wrapper: ReturnType<typeof mount> | null = null;
function mountDetail(p: Record<string, unknown> = {}) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  wrapper = mount(ProgressNodeDetail, {
    props: { modelValue: true, name: '財務二', state: '簽核中', runIds: [2, 5], stages: [], actions: [], schema: null, ...p },
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], i18n] },
    attachTo: document.body,
  });
  return wrapper;
}
afterEach(() => { wrapper?.unmount(); wrapper = null; document.body.innerHTML = ''; });
const body = () => document.body;
const ids = () => [...body().querySelectorAll('[data-testid^="stage-"]')].map((e) => e.getAttribute('data-testid'));

describe('ProgressNodeDetail', () => {
  it('lists the runs oldest first, like the timeline, with tasks and comments', async () => {
    mountDetail({
      stages: [stage(5, 5, { tasks: [task({ id: 50, assigneeName: '陳二' })] }), stage(2, 2, { status: 'returned', tasks: [task({ id: 20, status: 'rejected', actedTime: '2026-11-15T10:32:00' })] })],
      actions: [action(2, '附件不齊')],
    });
    await flushPromises();
    expect(ids()).toEqual(['stage-2', 'stage-5']); // 傳進來是新的在前，畫出來舊的在上
    expect(body().querySelector('[data-testid="task-20"]')!.textContent).toContain('王小華');
    expect(body().querySelector('[data-testid="log-comment"]')!.textContent).toBe('附件不齊');
    expect(body().querySelector('[data-testid="pg-detail-title"]')!.textContent).toBe('財務二 · 簽核中');
  });

  it('a node run twice lists both runs', async () => {
    mountDetail({ stages: [stage(2, 2, { status: 'returned' }), stage(5, 5)] });
    await flushPromises();
    expect(ids()).toHaveLength(2);
  });

  it('lists only the runs of this node', async () => {
    mountDetail({ runIds: [5], stages: [stage(2, 2), stage(5, 5), stage(9, 9)] });
    await flushPromises();
    expect(ids()).toEqual(['stage-5']);
  });

  it('only actions of that node\'s runs are listed', async () => {
    mountDetail({ stages: [stage(2, 2)], actions: [action(2, '這一關的意見'), action(7, '別的節點的意見'), action(null, '送出時的備註')] });
    await flushPromises();
    const comments = [...body().querySelectorAll('[data-testid="log-comment"]')].map((e) => e.textContent);
    expect(comments).toEqual(['這一關的意見']);
  });

  it('a changed node lists only the runs the reader can see', async () => {
    // 後端只把讀者看得到的段放進 runIds（stages 本來就是過濾後的）；沒有的段不會出現，也不會有它的歷程
    mountDetail({ name: '財務一', state: '已處理（之後有變動）', runIds: [2], stages: [stage(2, 2, { status: 'approved' })], actions: [action(2, '我同意'), action(5, '後續別人的意見')] });
    await flushPromises();
    expect(ids()).toEqual(['stage-2']);
    expect(body().textContent).not.toContain('後續別人的意見');
    expect(body().querySelector('[data-testid="pg-detail-title"]')!.textContent).toBe('財務一 · 已處理（之後有變動）');
  });

  it('shows only the name when there is no state text', async () => {
    mountDetail({ state: '', stages: [stage(2, 2)] });
    await flushPromises();
    expect(body().querySelector('[data-testid="pg-detail-title"]')!.textContent).toBe('財務二');
  });

  it('the close button closes it', async () => {
    const w = mountDetail({ stages: [stage(2, 2)] });
    await flushPromises();
    const btn = body().querySelector<HTMLElement>('[data-testid="pg-detail-close"]')!;
    expect(btn.getAttribute('aria-label')).toBe('關閉');
    btn.click();
    expect(w.emitted('update:modelValue')).toEqual([[false]]);
  });

  it('on a phone it is a bottom sheet', async () => {
    mountDetail({ bottom: true, stages: [stage(2, 2)] });
    await flushPromises();
    expect(body().querySelector('.q-dialog__inner--bottom')).not.toBeNull();
    expect(body().querySelector('.pg-detail-bottom')).not.toBeNull();
  });

  it('the dialog itself carries the accessible name, and the card adds no second dialog role', async () => {
    mountDetail({ stages: [stage(2, 2)] });
    await flushPromises();
    const dialogs = body().querySelectorAll('[role="dialog"]');
    expect(dialogs).toHaveLength(1);
    expect(dialogs[0].getAttribute('aria-label')).toBe('財務二 · 簽核中');
  });
});
