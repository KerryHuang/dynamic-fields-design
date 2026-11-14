import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { ApprovalActionView, ApprovalInstanceView, ApprovalStageView, ApprovalTaskView, RecordApproval } from 'src/domain/dynamic/approval-types';
import type { ModuleSchema, SchemaField } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import ApprovalPanel from './ApprovalPanel.vue';

const task = (p: Partial<ApprovalTaskView> = {}): ApprovalTaskView => ({
  id: 1, assigneeUserId: 'u1', assigneeName: '王主管', originalUserId: 'u1', reason: 'resolved', status: 'open', actedTime: null, canAct: false, canReturnPrevious: false, ...p,
});
const stage = (p: Partial<ApprovalStageView> = {}): ApprovalStageView => ({
  id: 1, seq: 1, stageCode: 's1', name: { 'zh-TW': '主管' }, mode: 'all', status: 'active', skipReason: null, dueTime: null, tasks: [task()], editableFieldKeys: [], ...p,
});
const action = (p: Partial<ApprovalActionView> = {}): ApprovalActionView => ({
  action: 'approve', actorUserId: 'u1', actorName: '王主管', onBehalfOfUserId: null, targetUserIds: [], comment: null, data: null, createdTime: '2026-11-12T08:30:00', ...p,
});
const instance = (p: Partial<ApprovalInstanceView> = {}): ApprovalInstanceView => ({
  id: 7, status: 'pending', routeKind: 'flow', flowName: { 'zh-TW': '採購簽核' }, fromOptionId: 1, toOptionId: 2, initiatorUserId: 'a',
  submittedTime: '2026-11-12T08:00:00', completedTime: null, rowVersion: 'iv', stages: [stage()], actions: [], canWithdraw: false, canCancel: false,
  titleSnapshot: '採購單', ...p,
});
const approval = (current: ApprovalInstanceView | null, previous: ApprovalInstanceView[] = []): RecordApproval => ({ current, previous, submitTargets: [] });

const base = { required: false, readOnly: false, showInList: true, isFilterable: true, isSortable: true };
const amount: SchemaField = { id: 3, key: 'f_3', type: 'text', label: { 'zh-TW': '金額' }, options: [], config: base };
const schema = {
  key: 'm', blocks: [{ id: 1, label: { 'zh-TW': '區' }, collapsed: false, columns: 2, fields: [amount] }],
} as unknown as ModuleSchema;

function mountPanel(a: RecordApproval, props: Record<string, unknown> = {}) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  return mount(ApprovalPanel, {
    props: { approval: a, schema, readonly: true, ...props },
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], i18n] },
  });
}
const text = (w: ReturnType<typeof mountPanel>, id: string) => w.find(`[data-testid="${id}"]`).text();

describe('ApprovalPanel', () => {
  it('lists stages in seq order with status labels', () => {
    const w = mountPanel(approval(instance({
      stages: [
        stage({ id: 2, seq: 2, name: { 'zh-TW': '財務' }, status: 'queued', tasks: [task({ id: 2, assigneeName: '李財務' })] }),
        stage({ id: 1, seq: 1, status: 'approved', tasks: [task({ status: 'approved', actedTime: '2026-11-12T09:00:00' })] }),
      ],
    })));
    const stages = w.findAll('[data-testid^="stage-"]');
    expect(stages.map((s) => s.attributes('data-testid'))).toEqual(['stage-1', 'stage-2']);
    expect(text(w, 'stage-1')).toContain('主管');
    expect(text(w, 'stage-1')).toContain('已同意');
    expect(text(w, 'stage-1')).toContain('王主管');
    expect(text(w, 'stage-2')).toContain('等待中');
    expect(text(w, 'stage-2')).toContain('李財務');
  });

  it('StageList shows who added the stage and queued status', () => {
    const w = mountPanel(approval(instance({
      stages: [
        stage({ id: 1, seq: 1, status: 'approved', tasks: [task({ status: 'approved' })] }),
        stage({ id: 2, seq: 2, stageCode: null, name: { 'zh-TW': '加簽' }, mode: 'any', status: 'active', addedByName: '進銷存主管', tasks: [task({ id: 2, assigneeName: '業務一', reason: 'added' })] }),
        stage({ id: 3, seq: 3, stageCode: null, name: { 'zh-TW': '加簽' }, status: 'queued', addedByName: '業務主管', tasks: [] }),
      ],
    })));
    expect(text(w, 'stage-2')).toContain('加簽');
    expect(text(w, 'stage-2')).toContain('由 進銷存主管 加簽');
    expect(text(w, 'stage-2')).toContain('業務一');
    expect(text(w, 'stage-3')).toContain('等待中');
    expect(text(w, 'stage-3')).toContain('由 業務主管 加簽');
    expect(w.find('[data-testid="stage-1"] [data-testid="stage-added-by"]').exists()).toBe(false);
  });

  it('a transferred task shows the original approver; a plain one does not', () => {
    const w = mountPanel(approval(instance({ stages: [stage({ tasks: [
      task({ id: 1, assigneeName: '李財務', reason: 'transferred', originalName: '王主管' }), task({ id: 2, assigneeName: '陳業務' }),
    ] })] })));
    expect(text(w, 'task-1')).toContain('轉交');
    expect(w.find('[data-testid="task-1"] [data-testid="task-original"]').text()).toBe('原簽核人 王主管');
    expect(w.find('[data-testid="task-2"] [data-testid="task-original"]').exists()).toBe(false);
  });

  it('a reassigned task reads 改派, never 轉交, for both the old and the new holder', () => {
    const w = mountPanel(approval(instance({ stages: [stage({ tasks: [
      task({ id: 1, assigneeName: '王主管', status: 'reassigned', reason: 'resolved' }),
      task({ id: 2, assigneeName: '李財務', reason: 'reassigned', originalName: '王主管' }),
    ] })] })));
    expect(text(w, 'task-1')).toContain('已改派');
    expect(text(w, 'task-1')).not.toContain('轉交');
    expect(text(w, 'task-2')).toContain('改派');
    expect(text(w, 'task-2')).not.toContain('轉交');
    expect(w.find('[data-testid="task-2"] [data-testid="task-original"]').text()).toBe('原簽核人 王主管');
  });

  it('shows the stage mode (all sign / any sign)', () => {
    const w = mountPanel(approval(instance({ stages: [stage({ mode: 'any' })] })));
    expect(text(w, 'stage-1')).toContain('或簽');
  });

  it('skipped stage shows 已跳過', () => {
    const w = mountPanel(approval(instance({ stages: [stage({ status: 'skipped', skipReason: 'condition', tasks: [] })] })));
    expect(text(w, 'stage-1')).toContain('已跳過');
  });

  it('fallback task shows 備援 badge', () => {
    const w = mountPanel(approval(instance({ stages: [stage({ tasks: [task({ reason: 'fallback' })] })] })));
    expect(w.find('[data-testid="task-fallback"]').text()).toContain('備援');
  });

  it('overdue active stage shows overdue mark; done stage does not', () => {
    const past = '2020-01-01T00:00:00';
    const w = mountPanel(approval(instance({ stages: [stage({ id: 1, dueTime: past }), stage({ id: 2, seq: 2, status: 'approved', dueTime: past })] })));
    expect(w.find('[data-testid="stage-1"] [data-testid="stage-overdue"]').exists()).toBe(true);
    expect(w.find('[data-testid="stage-2"] [data-testid="stage-overdue"]').exists()).toBe(false);
  });

  it('content-changed entry shows field label with before and after', () => {
    const w = mountPanel(approval(instance({
      actions: [action({ action: 'content-changed', data: [{ fieldId: 3, before: '100', after: '250' }, { fieldId: 99, before: 'x', after: 'y' }] })],
    })));
    const log = text(w, 'approval-log');
    expect(log).toContain('金額');
    expect(log).toContain('100');
    expect(log).toContain('250');
    expect(log).not.toContain('99');
  });

  it('without a schema content-changed lists no fields', () => {
    const w = mountPanel(approval(instance({
      actions: [action({ action: 'content-changed', data: [{ fieldId: 3, before: '100', after: '250' }] })],
    })), { schema: null });
    expect(text(w, 'approval-log')).not.toContain('250');
  });

  it.each([
    ['flow', '已改用新的流程重新簽核'],
    ['fallback', '改由備援簽核人重新簽核'],
    ['pick', '需由發起人重新送出'],
    [undefined, '流程已重來'],
  ])('reroute entry of kind %s is worded from the kind, not the stored Chinese note', (kind, expected) => {
    const w = mountPanel(approval(instance({ actions: [action({ action: 'reroute', actorName: '系統', comment: '儲存的繁中備註', data: kind ? { kind } : null })] })));
    expect(text(w, 'approval-log')).toContain('流程重來');
    expect(text(w, 'approval-log')).toContain(expected);
    expect(text(w, 'approval-log')).not.toContain('儲存的繁中備註');
  });

  it('fallback entry is worded from its kind, not the stored Chinese note', () => {
    const w = mountPanel(approval(instance({ actions: [action({ action: 'fallback', actorName: '系統', comment: '來源 purchase-lead 找不到簽核人，依設定改派備援簽核人', data: { kind: 'fallback' } })] })));
    expect(text(w, 'approval-log')).toContain('改由備援簽核人');
    expect(text(w, 'approval-log')).toContain('找不到簽核人，已依設定改由備援簽核人處理');
    expect(text(w, 'approval-log')).not.toContain('purchase-lead');
  });

  it('shows action, actor and comment', () => {
    const w = mountPanel(approval(instance({ actions: [action({ action: 'reject', comment: '金額不符' })] })));
    const log = text(w, 'approval-log');
    expect(log).toContain('退回發起人');
    expect(log).toContain('王主管');
    expect(log).toContain('金額不符');
  });

  it('previous instances are collapsed under 先前的簽核', () => {
    const w = mountPanel(approval(instance(), [instance({ id: 5, status: 'rerouted', flowName: { 'zh-TW': '舊流程' } })]));
    const exp = w.find('[data-testid="approval-previous"]');
    expect(exp.exists()).toBe(true);
    expect(exp.text()).toContain('先前的簽核');
    expect(exp.find('.q-expansion-item--collapsed').exists() || exp.classes().includes('q-expansion-item--collapsed')).toBe(true);
  });

  it('no previous instances: no 先前的簽核 section', () => {
    const w = mountPanel(approval(instance()));
    expect(w.find('[data-testid="approval-previous"]').exists()).toBe(false);
  });

  const ended = (id: number, status: ApprovalInstanceView['status'], extra: Partial<ApprovalInstanceView> = {}) =>
    instance({ id, status, flowName: { 'zh-TW': `流程${id}` }, stages: [stage({ id, status: 'approved' })], ...extra });

  it('with nothing running, the latest ended instance is the main block (not folded) and older ones stay folded', () => {
    const w = mountPanel(approval(null, [ended(9, 'rejected', { actions: [action({ action: 'reject', comment: '金額不符' })] }), ended(5, 'approved')]));
    expect(w.find('[data-testid="approval-main-status"]').text()).toBe('已退回');
    expect(text(w, 'approval-panel')).toContain('流程9');
    expect(text(w, 'approval-log')).toContain('金額不符');
    const folded = w.find('[data-testid="approval-previous"]');
    expect(folded.text()).toContain('先前的簽核');
    expect(folded.text()).toContain('1');
    expect(w.find('[data-testid="previous-9"]').exists()).toBe(false);
  });

  it('with nothing running and one ended instance there is no folded section', () => {
    const w = mountPanel(approval(null, [ended(9, 'approved')]));
    expect(w.find('[data-testid="approval-main-status"]').text()).toBe('已核准');
    expect(w.find('[data-testid="approval-previous"]').exists()).toBe(false);
  });

  it('a running instance stays the main block and every ended one is folded', () => {
    const w = mountPanel(approval(instance(), [ended(9, 'approved')]));
    expect(w.find('[data-testid="approval-main-status"]').text()).toBe('簽核中');
    expect(w.find('[data-testid="approval-previous"]').text()).toContain('1');
  });

  it('a rerouted instance in the folded list reads "flow restarted"; as the main block it asks to submit again', async () => {
    const folded = mountPanel(approval(instance(), [ended(5, 'rerouted')]));
    await folded.find('[data-testid="approval-previous"] .q-item').trigger('click');
    expect(folded.find('[data-testid="previous-5"]').text()).toContain('流程已重來');
    expect(folded.find('[data-testid="previous-5"]').text()).not.toContain('請重新送出');
    const main = mountPanel(approval(null, [ended(5, 'rerouted')]));
    expect(main.find('[data-testid="approval-main-status"]').text()).toContain('請重新送出');
  });
});
