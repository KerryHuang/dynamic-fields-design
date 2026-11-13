import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { ApproverSource, ApproverSourceKind } from 'src/domain/dynamic/approval-flow-types';
import type { DesignerField, ModuleDetail } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import ApproverSourceEditor from './ApproverSourceEditor.vue';
import { ALL_SOURCE_KINDS, FALLBACK_SOURCE_KINDS, type ApproverLimits } from './approver-source';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { getUsers: vi.fn(), getGroups: vi.fn() } }));
const api = vi.mocked(dynamicApi);

const userField: DesignerField = {
  id: 9, key: 'f_9', type: 'user', label: { 'zh-TW': '負責人' }, blockId: 1, sequence: 1, status: 'Active',
  config: { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false }, options: [],
};
const mod: ModuleDetail = {
  id: 1, key: 'doc', programCode: 'P', name: { 'zh-TW': '單據' }, status: 'Active', version: 1,
  blocks: [{ id: 1, label: { 'zh-TW': 'b' }, sequence: 1, collapsed: false, columns: 2, fields: [userField] }],
};

interface Opts { allowed?: ApproverSourceKind[]; limits?: ApproverLimits; saved?: ApproverSource | null; users?: 'pending' | 'ok'; groups?: 'ok' | 'fail' }
async function mountEditor(modelValue: ApproverSource, o: Opts = {}) {
  if (o.users === 'pending') api.getUsers.mockReturnValue(new Promise(() => {}));
  else api.getUsers.mockResolvedValue([{ userId: 'u1', displayName: '王小明' }] as never);
  if (o.groups === 'fail') api.getGroups.mockRejectedValue({ status: 500 });
  else api.getGroups.mockResolvedValue([{ id: 3, key: 'mgr', name: { 'zh-TW': '主管群' }, members: [] }]);
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(ApproverSourceEditor, {
    attachTo: document.body,
    props: { modelValue, module: mod, allowed: o.allowed ?? ALL_SOURCE_KINDS, limits: o.limits ?? { maxApprovers: 5, techMaxApprovers: 50, maxManagerLevel: 3 }, saved: o.saved, label: '來源' },
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], createPinia(), i18n,
      [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]] },
  });
  await flushPromises();
  return w;
}
const lastValue = (w: Awaited<ReturnType<typeof mountEditor>>) => w.emitted('update:modelValue')!.at(-1)![0];
// Quasar puts data-testid on the inner input, so selects are found by order: kind first, then the source's own params.
const select = (w: Awaited<ReturnType<typeof mountEditor>>, i: number) => w.findAllComponents(quasar.QSelect)[i]!;

describe('ApproverSourceEditor', () => {
  beforeEach(() => { vi.clearAllMocks(); document.body.innerHTML = ''; });

  it("switching source resets params to that source's defaults", async () => {
    const w = await mountEditor({ source: 'users', userIds: ['u1'] });
    select(w, 0).vm.$emit('update:modelValue', 'manager');
    expect(lastValue(w)).toEqual({ source: 'manager', level: 1 });
    select(w, 0).vm.$emit('update:modelValue', 'initiatorPick');
    expect(lastValue(w)).toEqual({ source: 'initiatorPick', candidateGroupIds: [], min: 1, max: 1 });
    w.unmount();
  });

  it('manager level options run from 1 to the policy max', async () => {
    const w = await mountEditor({ source: 'manager', level: 1 });
    expect((select(w, 1).props('options') as { value: number }[]).map((o) => o.value)).toEqual([1, 2, 3]);
    w.unmount();
    const legacy = await mountEditor({ source: 'manager', level: 5 }, { saved: { source: 'manager', level: 5 } });
    const opts = select(legacy, 1).props('options') as { value: number; label: string }[];
    expect(opts.map((o) => o.value)).toEqual([1, 2, 3, 4, 5]);
    expect(opts[4]!.label).toContain('沿用舊值');
    legacy.unmount();
  });

  it('users hint names the save limit and the technical limit', async () => {
    const w = await mountEditor({ source: 'users', userIds: [] });
    expect(w.find('[data-testid="source-hint"]').text()).toBe('存檔時最多 5 位（模組政策）。名單存檔後，執行時只受技術上限 50 位限制。');
    w.unmount();
  });

  it('group hint says the current per-stage limit applies at activation', async () => {
    const w = await mountEditor({ source: 'group', groupIds: [3] });
    expect(w.find('[data-testid="source-hint"]').text()).toBe('每一關啟動時，解析出的人數不能超過當時的每關人數上限（目前 5 位）。');
    w.unmount();
  });

  it('pick hint explains the effective max and the min clamp', async () => {
    const w = await mountEditor({ source: 'initiatorPick', min: 1, max: 2, candidateGroupIds: [] });
    const hint = w.find('[data-testid="source-hint"]').text();
    expect(hint).toContain('最多人數不能超過 5');
    expect(hint).toContain('最少人數比實際上限大時，改用實際上限');
    w.unmount();
  });

  it('fallback mode offers only users, group and manager with the fallback hint', async () => {
    const w = await mountEditor({ source: 'users', userIds: [] }, { allowed: FALLBACK_SOURCE_KINDS });
    expect((select(w, 0).props('options') as { value: string }[]).map((o) => o.value)).toEqual(['users', 'group', 'manager']);
    expect(w.find('[data-testid="source-hint"]').text()).toContain('備援簽核人頂替找不到人的來源時');
    expect(w.find('[data-testid="source-hint"]').text()).toContain('50 位');
    w.unmount();
  });

  it('no hint number is invented when the policy cannot be read', async () => {
    const w = await mountEditor({ source: 'group', groupIds: [] }, { limits: {} });
    expect(w.find('[data-testid="source-hint"]').exists()).toBe(false);
    w.unmount();
  });

  it('user chips show missing only after users load', async () => {
    const pending = await mountEditor({ source: 'users', userIds: ['gone'] }, { users: 'pending' });
    expect(document.body.textContent).toContain('gone');
    expect(document.body.textContent).not.toContain('找不到或已停用');
    pending.unmount();
    const loaded = await mountEditor({ source: 'users', userIds: ['gone'] });
    expect(document.body.textContent).toContain('找不到或已停用');
    loaded.unmount();
  });

  it('group chips do not claim missing when the groups request failed', async () => {
    const w = await mountEditor({ source: 'group', groupIds: [77] }, { groups: 'fail' });
    expect(document.body.textContent).toContain('77');
    expect(document.body.textContent).not.toContain('找不到或已停用');
    w.unmount();
  });

  it('userField without candidate groups shows the error', async () => {
    const w = await mountEditor({ source: 'userField', fieldId: 9, candidateGroupIds: [] });
    expect(w.find('[data-testid="source-candidates-error"]').text()).toContain('候選群組');
    w.unmount();
    const ok = await mountEditor({ source: 'userField', fieldId: 9, candidateGroupIds: [3] });
    expect(ok.find('[data-testid="source-candidates-error"]').exists()).toBe(false);
    ok.unmount();
  });

  it('pick min and max edit as numbers', async () => {
    const w = await mountEditor({ source: 'initiatorPick', min: 1, max: 1, candidateGroupIds: [] });
    w.findAllComponents(quasar.QInput)[1]!.vm.$emit('update:modelValue', '3');
    expect(lastValue(w)).toMatchObject({ max: 3, min: 1 });
    w.unmount();
  });

  it('every input has a label', async () => {
    for (const s of [
      { source: 'users', userIds: [] }, { source: 'group', groupIds: [] }, { source: 'manager', level: 1 },
      { source: 'userField', fieldId: 9, candidateGroupIds: [] }, { source: 'initiatorPick', min: 1, max: 1, candidateGroupIds: [] },
    ] as ApproverSource[]) {
      const w = await mountEditor(s);
      const inputs = [...w.findAllComponents(quasar.QSelect), ...w.findAllComponents(quasar.QInput)];
      expect(inputs.length).toBeGreaterThan(0);
      for (const c of inputs) expect((c as { props: (k: string) => unknown }).props('label')).toBeTruthy();
      w.unmount();
    }
  });
});
