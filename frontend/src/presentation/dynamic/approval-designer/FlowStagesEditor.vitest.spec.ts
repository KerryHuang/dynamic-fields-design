import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { StageDraft } from 'src/domain/dynamic/approval-flow-draft';
import { buildFlowInput, draftFromFlow } from 'src/domain/dynamic/approval-flow-draft';
import type { ApprovalFlow, ApprovalStageDef } from 'src/domain/dynamic/approval-flow-types';
import type { DesignerField, ModuleDetail } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import ApproverSourceEditor from './ApproverSourceEditor.vue';
import FlowStagesEditor from './FlowStagesEditor.vue';
import type { FlowLimits } from './use-approval-limits';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { getUsers: vi.fn(), getGroups: vi.fn() } }));
const api = vi.mocked(dynamicApi);

const field = (id: number, type: string, label: string): DesignerField => ({
  id, key: `f_${id}`, type: type as never, label: { 'zh-TW': label }, blockId: 1, sequence: id, status: 'Active',
  config: { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false }, options: [],
});
const mod: ModuleDetail = {
  id: 1, key: 'doc', programCode: 'P', name: { 'zh-TW': '單據' }, status: 'Active', version: 1,
  blocks: [{ id: 1, label: { 'zh-TW': 'b' }, sequence: 1, collapsed: false, columns: 2, fields: [field(7, 'decimal', '總額'), field(8, 'text', '備註')] }],
};
const stage = (code: string, extra: Partial<StageDraft> = {}): StageDraft => ({
  code, name: { 'zh-TW': code }, approvers: { source: 'group', groupIds: [3] }, mode: 'all', editableFields: [], dueHours: null, onEmpty: 'fallback', ...extra,
});

interface Opts { saved?: ApprovalStageDef[]; limits?: FlowLimits; errors?: Record<string, string[]>; editableFieldIds?: number[] }
async function mountEditor(stages: StageDraft[], o: Opts = {}) {
  api.getUsers.mockResolvedValue([]);
  api.getGroups.mockResolvedValue([{ id: 3, key: 'mgr', name: { 'zh-TW': '主管群' }, members: [] }]);
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(FlowStagesEditor, {
    attachTo: document.body,
    props: {
      modelValue: stages, module: mod, editableFieldIds: o.editableFieldIds ?? [7], limits: o.limits ?? { maxStages: 5, maxDueHours: 100 }, errors: o.errors, saved: o.saved,
      'onUpdate:modelValue': (v: StageDraft[]) => w.setProps({ modelValue: v }),
    },
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], createPinia(), i18n,
      [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]] },
  });
  await flushPromises();
  return w;
}
type W = Awaited<ReturnType<typeof mountEditor>>;
const codes = (w: W) => (w.props('modelValue') as StageDraft[]).map((s) => s.code);
const body = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

describe('FlowStagesEditor', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    document.body.innerHTML = '';
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
    quasar.Screen.setDebounce(0);
    window.innerWidth = 1024;
    window.dispatchEvent(new Event('resize'));
  });

  it('plus between cards inserts a stage at that position with a fresh stage-N code', async () => {
    const w = await mountEditor([stage('stage-1'), stage('stage-3')]);
    await w.find('[data-testid="stage-insert-1"]').trigger('click');
    expect(codes(w)).toEqual(['stage-1', 'stage-2', 'stage-3']);
    await w.find('[data-testid="stage-insert-0"]').trigger('click');
    expect(codes(w)).toEqual(['stage-4', 'stage-1', 'stage-2', 'stage-3']);
    w.unmount();
  });

  it('add is disabled at the policy stage limit', async () => {
    const w = await mountEditor([stage('a'), stage('b')], { limits: { maxStages: 2 } });
    expect(w.find('[data-testid="stage-insert-0"]').attributes('disabled')).toBeDefined();
    expect(w.find('[data-testid="stage-insert-2"]').attributes('disabled')).toBeDefined();
    expect(w.find('[data-testid="stages-limit"]').text()).toContain('2');
    w.unmount();
  });

  it('no limit shown or enforced when policies fail to load', async () => {
    const w = await mountEditor([stage('a'), stage('b')], { limits: {} });
    expect(w.find('[data-testid="stages-limit"]').exists()).toBe(false);
    expect(w.find('[data-testid="stage-insert-2"]').attributes('disabled')).toBeUndefined();
    w.unmount();
  });

  it('typing a name never changes the code', async () => {
    const w = await mountEditor([stage('stage-1')]);
    const nameInput = w.findAll('[data-testid="i18n-zh-TW"] input')[0]!;
    await nameInput.setValue('部門主管簽核');
    const s = (w.props('modelValue') as StageDraft[])[0]!;
    expect(s.name['zh-TW']).toBe('部門主管簽核');
    expect(s.code).toBe('stage-1');
    w.unmount();
  });

  it('changing a saved stage code warns it counts as a new stage; a new stage has no warning', async () => {
    const w = await mountEditor([stage('a', { savedCode: 'a' }), stage('stage-2')]);
    expect(w.find('[data-testid="stage-code-warning-0"]').exists()).toBe(false);
    await w.find('[data-testid="stage-code-0"]').setValue('renamed');
    expect(w.find('[data-testid="stage-code-warning-0"]').text()).toContain('新關卡');
    await w.find('[data-testid="stage-code-1"]').setValue('other');
    expect(w.find('[data-testid="stage-code-warning-1"]').exists()).toBe(false);
    w.unmount();
  });

  it('mode hidden for manager and a single user; shown for group', async () => {
    const w = await mountEditor([
      stage('m', { approvers: { source: 'manager', level: 1 } }),
      stage('u', { approvers: { source: 'users', userIds: ['a'] } }),
      stage('g'),
    ]);
    expect(w.find('[data-testid="stage-mode-0"]').exists()).toBe(false);
    expect(w.find('[data-testid="stage-mode-1"]').exists()).toBe(false);
    expect(w.find('[data-testid="stage-mode-2"]').exists()).toBe(true);
    w.unmount();
  });

  it('editable field options come from the workflow candidates', async () => {
    const w = await mountEditor([stage('a')], { editableFieldIds: [7] });
    const sel = w.findAllComponents(quasar.QSelect).find((c) => c.props('label') === '可編輯欄位')!;
    expect((sel.props('options') as { value: number; label: string }[])).toEqual([{ value: 7, label: '總額' }]);
    w.unmount();
  });

  it('due hours blank saves null; the max comes from policy', async () => {
    const w = await mountEditor([stage('a', { dueHours: 24 })]);
    const input = w.find('[data-testid="stage-due-0"]');
    expect(w.text()).toContain('上限 100 小時');
    await input.setValue('');
    expect((w.props('modelValue') as StageDraft[])[0]!.dueHours).toBeNull();
    await input.setValue('48');
    expect((w.props('modelValue') as StageDraft[])[0]!.dueHours).toBe(48);
    w.unmount();
  });

  it('skip shows the fewer-approvals warning', async () => {
    const w = await mountEditor([stage('a', { onEmpty: 'skip' }), stage('b')]);
    expect(w.find('[data-testid="stage-skip-warning-0"]').text()).toContain('少一道核准');
    expect(w.find('[data-testid="stage-skip-warning-1"]').exists()).toBe(false);
    w.unmount();
  });

  it('move and delete; the last stage cannot be deleted', async () => {
    const w = await mountEditor([stage('a'), stage('b')]);
    await w.find('[data-testid="stage-down-0"]').trigger('click');
    expect(codes(w)).toEqual(['b', 'a']);
    await w.find('[data-testid="stage-remove-0"]').trigger('click');
    expect(codes(w)).toEqual(['a']);
    expect(w.find('[data-testid="stage-remove-0"]').attributes('disabled')).toBeDefined();
    w.unmount();
  });

  it('definition.stages[1].approvers error shows on the second card only', async () => {
    const w = await mountEditor([stage('a'), stage('b')], { errors: { 'definition.stages[1].approvers': ['這一關找不到人'] } });
    expect(w.find('[data-testid="stage-card-1"]').text()).toContain('這一關找不到人');
    expect(w.find('[data-testid="stage-card-0"]').text()).not.toContain('這一關找不到人');
    w.unmount();
  });

  it('mobile width moves the card actions into the menu', async () => {
    window.innerWidth = 390;
    window.dispatchEvent(new Event('resize'));
    const w = await mountEditor([stage('a'), stage('b')]);
    expect(w.find('[data-testid="stage-down-0"]').exists()).toBe(false);
    await w.find('[data-testid="stage-menu-0"]').trigger('click');
    body('stage-down-0')!.click();
    await flushPromises();
    expect(codes(w)).toEqual(['b', 'a']);
    w.unmount();
  });
  const savedDef = (code: string, extra: Partial<ApprovalStageDef> = {}): ApprovalStageDef => ({
    code, name: { 'zh-TW': code }, when: null, approvers: { source: 'group', groupIds: [3] }, mode: 'all', editableFields: [], dueHours: null, onEmpty: 'fallback', ...extra,
  });

  it('a stage added after deleting a saved stage does not take the saved stage code', async () => {
    const saved = [savedDef('stage-1'), savedDef('stage-2')];
    const w = await mountEditor([stage('stage-1', { savedCode: 'stage-1' })], { saved });
    await w.find('[data-testid="stage-insert-1"]').trigger('click');
    expect(codes(w)).toEqual(['stage-1', 'stage-3']);
    w.unmount();
  });

  it('typing a saved stage code onto another stage warns it reuses that stage', async () => {
    const saved = [savedDef('a'), savedDef('b')];
    const w = await mountEditor([stage('a', { savedCode: 'a' }), stage('stage-1')], { saved });
    expect(w.find('[data-testid="stage-code-reused-1"]').exists()).toBe(false);
    await w.find('[data-testid="stage-code-1"]').setValue('b');
    expect(w.find('[data-testid="stage-code-reused-1"]').text()).toContain('b');
    w.unmount();
  });

  it('a renamed stage no longer offers the saved manager level above the policy max', async () => {
    const saved = [savedDef('a', { approvers: { source: 'manager', level: 5 } })];
    const limits = { maxStages: 5, maxManagerLevel: 3 };
    const stg = stage('a', { savedCode: 'a', approvers: { source: 'manager', level: 5 }, mode: 'any' });
    const same = await mountEditor([stg], { saved, limits });
    const levels = (w: W) => (w.findAllComponents(quasar.QSelect).find((c) => c.props('label') === '主管層級')!.props('options') as { value: number }[]).map((o) => o.value);
    expect(levels(same)).toEqual([1, 2, 3, 4, 5]);
    same.unmount();
    const renamed = await mountEditor([{ ...stg, code: 'z' }], { saved, limits });
    expect(levels(renamed)).toEqual([1, 2, 3]);
    renamed.unmount();
  });

  it('choosing a single-person source sets mode any', async () => {
    const w = await mountEditor([stage('a', { mode: 'all' })]);
    const kind = w.findAllComponents(quasar.QSelect).find((c) => c.props('label') === '簽核人來源')!;
    kind.vm.$emit('update:modelValue', 'manager');
    await flushPromises();
    expect((w.props('modelValue') as StageDraft[])[0]!.mode).toBe('any');
    w.unmount();
  });

  it('a group source keeps the chosen mode', async () => {
    const w = await mountEditor([stage('a', { mode: 'all' })]);
    w.findComponent(ApproverSourceEditor).vm.$emit('update:modelValue', { source: 'group', groupIds: [3, 4] });
    await flushPromises();
    expect((w.props('modelValue') as StageDraft[])[0]!.mode).toBe('all');
    w.unmount();
  });

  it('removing a person and adding them back keeps all-must-approve and the saved definition', async () => {
    const approvers = { source: 'users' as const, userIds: ['u1', 'u2'] };
    const def = savedDef('a', { approvers, mode: 'all' });
    const flow = { key: 'k', name: {}, toOptionId: 12, priority: 1, active: true, definition: { shape: 'sequence' as const, when: null, stages: [def] } } as unknown as ApprovalFlow;
    const draft = draftFromFlow(flow);
    const w = await mountEditor(draft.stages, { saved: [def] });
    const set = (ids: string[]) => w.findComponent(ApproverSourceEditor).vm.$emit('update:modelValue', { source: 'users', userIds: ids });
    set(['u1']);
    await flushPromises();
    expect((w.props('modelValue') as StageDraft[])[0]!.mode).toBe('any');
    set(['u1', 'u2']);
    await flushPromises();
    const stages = w.props('modelValue') as StageDraft[];
    expect(stages[0]!.mode).toBe('all');
    expect(buildFlowInput({ ...draft, stages })).toEqual(buildFlowInput(draft));
    expect(JSON.stringify(buildFlowInput({ ...draft, stages }))).not.toContain('chosenMode');
    w.unmount();
  });

  it('a group stage switched to an empty user list and then filled keeps its mode', async () => {
    const w = await mountEditor([stage('a', { mode: 'all' })]);
    const set = (ids: string[]) => w.findComponent(ApproverSourceEditor).vm.$emit('update:modelValue', { source: 'users', userIds: ids });
    set([]);
    await flushPromises();
    set(['u1', 'u2', 'u3']);
    await flushPromises();
    expect((w.props('modelValue') as StageDraft[])[0]!.mode).toBe('all');
    w.unmount();
  });

  it('a saved stage renamed to another saved code shows only the reuse warning', async () => {
    const saved = [savedDef('a'), savedDef('b')];
    const w = await mountEditor([stage('a', { savedCode: 'a' }), stage('b', { savedCode: 'b' })], { saved });
    await w.find('[data-testid="stage-code-1"]').setValue('a');
    expect(w.find('[data-testid="stage-code-reused-1"]').exists()).toBe(true);
    expect(w.find('[data-testid="stage-code-warning-1"]').exists()).toBe(false);
    await w.find('[data-testid="stage-code-1"]').setValue('new');
    expect(w.find('[data-testid="stage-code-warning-1"]').exists()).toBe(true);
    expect(w.find('[data-testid="stage-code-reused-1"]').exists()).toBe(false);
    w.unmount();
  });

  it('insert is allowed up to the saved stage count when the policy limit was tightened below it', async () => {
    const saved = [1, 2, 3, 4, 5, 6].map((n) => savedDef(`s${n}`));
    const w = await mountEditor(saved.slice(0, 5).map((s) => stage(s.code, { savedCode: s.code })), { saved, limits: { maxStages: 5 } });
    expect(w.find('[data-testid="stage-insert-5"]').attributes('disabled')).toBeUndefined();
    await w.find('[data-testid="stage-insert-5"]').trigger('click');
    expect((w.props('modelValue') as StageDraft[]).length).toBe(6);
    expect(w.find('[data-testid="stage-insert-6"]').attributes('disabled')).toBeDefined();
    w.unmount();
  });

  it('insert buttons say where they insert', async () => {
    const w = await mountEditor([stage('a'), stage('b')]);
    expect(w.find('[data-testid="stage-insert-1"]').attributes('aria-label')).toBe('在第 2 關之前插入關卡');
    expect(w.find('[data-testid="stage-insert-2"]').attributes('aria-label')).toBe('在最後插入關卡');
    w.unmount();
  });
});
