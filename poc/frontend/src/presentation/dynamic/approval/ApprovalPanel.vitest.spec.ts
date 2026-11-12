import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { ApprovalActionView, ApprovalInstanceView, ApprovalStageView, ApprovalTaskView, RecordApproval } from 'src/domain/dynamic/approval-types';
import type { ModuleSchema, SchemaField } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import ApprovalPanel from './ApprovalPanel.vue';

const task = (p: Partial<ApprovalTaskView> = {}): ApprovalTaskView => ({
  id: 1, assigneeUserId: 'u1', assigneeName: '王主管', originalUserId: 'u1', reason: 'resolved', status: 'open', actedTime: null, canAct: false, ...p,
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

  it('reroute entry shows its note', () => {
    const w = mountPanel(approval(instance({ actions: [action({ action: 'reroute', actorName: '系統', comment: '單據內容已變，改用流程「X」重新簽核' })] })));
    expect(text(w, 'approval-log')).toContain('流程重來');
    expect(text(w, 'approval-log')).toContain('單據內容已變，改用流程「X」重新簽核');
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
});
