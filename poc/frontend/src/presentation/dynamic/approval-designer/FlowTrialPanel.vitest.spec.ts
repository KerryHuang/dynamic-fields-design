import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import { emptyFlowDraft, type FlowDraft } from 'src/domain/dynamic/approval-flow-draft';
import type { ApprovalFlow } from 'src/domain/dynamic/approval-flow-types';
import type { ApprovalPreview } from 'src/domain/dynamic/approval-types';
import type { DesignerField, ModuleDetail } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { fieldTypeRegistry } from 'src/presentation/dynamic/field-type-registry';
import FlowTrialPanel from './FlowTrialPanel.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: { getUsers: vi.fn(), previewApprovalDraft: vi.fn(), getPagedRecords: vi.fn() },
}));
const api = vi.mocked(dynamicApi);

const field = (id: number, type: string, label: string): DesignerField => ({
  id, key: `f_${id}`, type: type as never, label: { 'zh-TW': label }, blockId: 1, sequence: id, status: 'Active',
  config: { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false }, options: [],
});
const mod: ModuleDetail = {
  id: 1, key: 'doc', programCode: 'P', name: { 'zh-TW': '單據' }, status: 'Active', version: 1,
  blocks: [{ id: 1, label: { 'zh-TW': 'b' }, sequence: 1, collapsed: false, columns: 2, fields: [field(7, 'decimal', '總額'), field(8, 'decimal', '備註數')] }],
};
const stage = (code: string, approvers: ApprovalFlow['definition']['stages'][0]['approvers'] = { source: 'manager', level: 1 }) =>
  ({ code, name: { 'zh-TW': code }, when: null, approvers, mode: 'any' as const, editableFields: [], dueHours: null, onEmpty: 'fallback' as const });
const saved = (key: string, to: number, priority: number, when: ApprovalFlow['definition']['when'] = null): ApprovalFlow => ({
  key, name: { 'zh-TW': `流程${key}` }, toOptionId: to, priority, active: true, moduleKey: 'doc', version: 1, used: false, pendingCount: 0,
  definition: { shape: 'sequence', when, rejectTo: null, fallbackApprovers: null, stages: [stage('s1')] },
});

function draftOf(extra: Partial<FlowDraft> = {}): FlowDraft {
  return { ...emptyFlowDraft(12, 1), key: 'mine', name: { 'zh-TW': '我的' }, when: { all: [{ field: 7, op: 'gte', value: 100 }] }, ...extra };
}
const preview = (extra: Partial<ApprovalPreview>): ApprovalPreview => ({ outcome: 'flow', flowName: { 'zh-TW': '我的' }, flowKey: 'mine', stages: [], picks: [], ...extra });

async function mountPanel(draft: FlowDraft = draftOf(), flows: ApprovalFlow[] = []) {
  api.getUsers.mockResolvedValue([{ userId: 'u1', displayName: '王小明' }] as never);
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(FlowTrialPanel, {
    attachTo: document.body, props: { module: mod, draft, flows },
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], createPinia(), i18n,
      [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]] },
  });
  await flushPromises();
  return w;
}
type W = Awaited<ReturnType<typeof mountPanel>>;
const select = (w: W, label: string) => w.findAllComponents(quasar.QSelect).find((c) => c.props('label') === label)!;
const el = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
const text = (id: string) => el(id)?.textContent ?? '';
async function chooseInitiator(w: W) { select(w, '發起人').vm.$emit('update:modelValue', 'u1'); await flushPromises(); }
async function run() { el('trial-run')!.click(); await flushPromises(); }

describe('FlowTrialPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    document.body.innerHTML = '';
    api.getPagedRecords.mockResolvedValue({ items: [], totalCount: 0, currentPage: 1, pagesSize: 20 });
  });

  it('trial button disabled until an initiator is chosen', async () => {
    const w = await mountPanel();
    expect(el('trial-run')!.hasAttribute('disabled') || el('trial-run')!.getAttribute('aria-disabled') === 'true').toBe(true);
    await run();
    expect(api.previewApprovalDraft).not.toHaveBeenCalled();
    await chooseInitiator(w);
    expect(el('trial-run')!.hasAttribute('disabled')).toBe(false);
  });

  it('trial posts the current draft, initiator, record and overridden values keyed f_id', async () => {
    api.previewApprovalDraft.mockResolvedValue(preview({}));
    api.getPagedRecords.mockResolvedValue({ items: [{ id: 9, title: '設計案 A', values: { f_7: 500 } } as never], totalCount: 1, currentPage: 1, pagesSize: 20 });
    const w = await mountPanel();
    await chooseInitiator(w);
    const rec = select(w, '記錄（選填）');
    const done = (fn: () => void) => fn();
    rec.vm.$emit('filter', '設計', done);
    await flushPromises();
    expect(api.getPagedRecords).toHaveBeenCalledWith('doc', { currentPage: 1, pagesSize: 20, q: '設計' });
    rec.vm.$emit('update:modelValue', 9);
    await flushPromises();
    w.findAllComponents(fieldTypeRegistry.decimal.component)[0]!.vm.$emit('update:modelValue', 800);
    await flushPromises();
    await run();
    const body = api.previewApprovalDraft.mock.calls[0]![1];
    expect(api.previewApprovalDraft.mock.calls[0]![0]).toBe('doc');
    expect(body.initiatorUserId).toBe('u1');
    expect(body.recordId).toBe(9);
    expect(body.values).toEqual({ f_7: 800 });
    expect(body.draft.key).toBe('mine');
    expect(body.draft.active).toBe(true);
    expect(body.draft.definition.when).toEqual({ all: [{ field: 7, op: 'gte', value: 100 }] });
  });

  it('override rows list only trial fields and prefill from the record', async () => {
    api.getPagedRecords.mockResolvedValue({ items: [{ id: 9, title: 'A', values: { f_7: 500, f_8: 3 } } as never], totalCount: 1, currentPage: 1, pagesSize: 20 });
    const w = await mountPanel();
    expect(w.findAllComponents(fieldTypeRegistry.decimal.component)).toHaveLength(1);
    const rec = select(w, '記錄（選填）');
    rec.vm.$emit('filter', '', (fn: () => void) => fn());
    await flushPromises();
    rec.vm.$emit('update:modelValue', 9);
    await flushPromises();
    expect((w.findAllComponents(fieldTypeRegistry.decimal.component)[0]!.vm.$props as { modelValue: unknown }).modelValue).toBe(500);
  });

  it('other flow matched shows the which-flow banner', async () => {
    api.previewApprovalDraft.mockResolvedValue(preview({ flowKey: 'other', flowName: { 'zh-TW': '別的流程' }, stages: [{ code: 's1', name: { 'zh-TW': 'S1' }, status: 'enter', approvers: [{ userId: 'u2', displayName: '李主管' }], usesFallback: false }] }));
    const w = await mountPanel(draftOf(), [saved('other', 12, 0)]);
    await chooseInitiator(w);
    await run();
    expect(text('trial-other-flow')).toBe('這筆會先符合流程「流程other」（優先序在前），不會走這個流程');
    expect(el('trial-flow')).toBeNull();
    expect(text('preview-stages')).toContain('李主管');
  });

  it('own flow matched shows its name and timeline', async () => {
    api.previewApprovalDraft.mockResolvedValue(preview({ stages: [{ code: 's1', name: { 'zh-TW': 'S1' }, status: 'enter', approvers: [{ userId: 'u2', displayName: '李主管' }], usesFallback: false }] }));
    const w = await mountPanel();
    await chooseInitiator(w);
    await run();
    expect(text('trial-flow')).toBe('流程：我的');
    expect(el('trial-other-flow')).toBeNull();
  });

  it('blocked and direct outcomes show their texts', async () => {
    const w = await mountPanel();
    await chooseInitiator(w);
    api.previewApprovalDraft.mockResolvedValueOnce(preview({ outcome: 'blocked', flowKey: null, flowName: null }));
    await run();
    expect(text('trial-blocked')).toBe('沒有流程符合，送出會被擋下');
    api.previewApprovalDraft.mockResolvedValueOnce(preview({ outcome: 'direct', flowKey: null, flowName: null }));
    await run();
    expect(text('trial-direct')).toBe('沒有流程符合，會直接轉移');
    expect(el('trial-blocked')).toBeNull();
  });

  it('stage problem and fallback badge are shown; skipped stage shows the skip text; pick stage shows the initiator note', async () => {
    api.previewApprovalDraft.mockResolvedValue(preview({
      stages: [
        { code: 'a', name: { 'zh-TW': 'A' }, status: 'enter', approvers: [], usesFallback: false, problem: 'noApprover' },
        { code: 'b', name: { 'zh-TW': 'B' }, status: 'enter', approvers: [{ userId: 'u3', displayName: '備援人' }], usesFallback: true },
        { code: 'c', name: { 'zh-TW': 'C' }, status: 'skip', approvers: [], usesFallback: false },
        { code: 'd', name: { 'zh-TW': 'D' }, status: 'pick', approvers: [], usesFallback: false },
        { code: 'e', name: { 'zh-TW': 'E' }, status: 'enter', approvers: [], usesFallback: false, problem: 'tooManyApprovers' },
      ],
    }));
    const w = await mountPanel();
    await chooseInitiator(w);
    await run();
    expect(text('preview-problem-a')).toBe('找不到簽核人，送出或推進到這一關時會失敗');
    expect(text('preview-problem-e')).toBe('人數超過每關上限，送出或推進到這一關時會失敗');
    expect(text('preview-stage-b')).toContain('備援');
    expect(text('preview-stage-c')).toContain('不需簽核');
    expect(text('preview-stage-d')).toContain('由發起人送出時選擇');
    expect(el('preview-problem-b')).toBeNull();
  });

  it('draft 400 lists the errors instead of a timeline', async () => {
    api.previewApprovalDraft.mockRejectedValue({ status: 400, errors: { 'definition.stages[0].approvers': ['群組不存在'] } });
    const w = await mountPanel();
    await chooseInitiator(w);
    await run();
    expect(text('trial-errors')).toContain('群組不存在');
    expect(el('preview-stages')).toBeNull();
    expect(el('trial-result')).toBeNull();
  });

  it('record search 403 shows the no-read note', async () => {
    api.getPagedRecords.mockRejectedValue({ status: 403 });
    const w = await mountPanel();
    select(w, '記錄（選填）').vm.$emit('filter', '', vi.fn());
    await flushPromises();
    expect(text('trial-record-forbidden')).toBe('你沒有這個模組的讀取權，請改用欄位值試算');
  });

  it('an edit to the draft clears the shown result', async () => {
    api.previewApprovalDraft.mockResolvedValue(preview({}));
    const w = await mountPanel();
    await chooseInitiator(w);
    await run();
    expect(el('trial-result')).not.toBeNull();
    await w.setProps({ draft: draftOf({ name: { 'zh-TW': '改名' }, when: { all: [{ field: 7, op: 'gte', value: 50 }] } }) });
    await flushPromises();
    expect(el('trial-result')).toBeNull();
  });

  it('without a target state it asks for one and sends nothing', async () => {
    await mountPanel(draftOf({ toOptionId: null }));
    expect(text('trial-need-target')).toBe('請先在「基本」選擇目標狀態');
    expect(el('trial-run')).toBeNull();
  });
});
