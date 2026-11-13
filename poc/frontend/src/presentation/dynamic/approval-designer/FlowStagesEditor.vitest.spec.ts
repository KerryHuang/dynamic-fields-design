import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { StageDraft } from 'src/domain/dynamic/approval-flow-draft';
import type { DesignerField, ModuleDetail } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
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

interface Opts { limits?: FlowLimits; errors?: Record<string, string[]>; editableFieldIds?: number[] }
async function mountEditor(stages: StageDraft[], o: Opts = {}) {
  api.getUsers.mockResolvedValue([]);
  api.getGroups.mockResolvedValue([{ id: 3, key: 'mgr', name: { 'zh-TW': '主管群' }, members: [] }]);
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(FlowStagesEditor, {
    attachTo: document.body,
    props: {
      modelValue: stages, module: mod, editableFieldIds: o.editableFieldIds ?? [7], limits: o.limits ?? { maxStages: 5, maxDueHours: 100 }, errors: o.errors,
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
});
