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

interface Opts { fallback?: boolean; allowed?: ApproverSourceKind[]; limits?: ApproverLimits; saved?: ApproverSource | null; users?: 'pending' | 'ok'; groups?: 'ok' | 'fail' }
async function mountEditor(modelValue: ApproverSource, o: Opts = {}) {
  if (o.users === 'pending') api.getUsers.mockReturnValue(new Promise(() => {}));
  else api.getUsers.mockResolvedValue([{ userId: 'u1', displayName: '王小明' }] as never);
  if (o.groups === 'fail') api.getGroups.mockRejectedValue({ status: 500 });
  else api.getGroups.mockResolvedValue([{ id: 3, key: 'mgr', name: { 'zh-TW': '主管群' }, members: [] }]);
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(ApproverSourceEditor, {
    attachTo: document.body,
    props: { modelValue, module: mod, allowed: o.allowed ?? ALL_SOURCE_KINDS, limits: o.limits ?? { maxApprovers: 5, techMaxApprovers: 50, maxManagerLevel: 3 }, saved: o.saved, label: '來源', fallback: o.fallback },
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
    const w = await mountEditor({ source: 'users', userIds: [] }, { allowed: FALLBACK_SOURCE_KINDS, fallback: true });
    expect((select(w, 0).props('options') as { value: string }[]).map((o) => o.value)).toEqual(['users', 'group', 'manager']);
    expect(w.find('[data-testid="source-hint"]').text()).toContain('備援簽核人頂替找不到人的來源時');
    expect(w.find('[data-testid="source-hint"]').text()).toContain('50 位');
    w.unmount();
  });

  it('a stage with only three allowed sources does not turn into the fallback hint without the fallback prop', async () => {
    const w = await mountEditor({ source: 'group', groupIds: [3] }, { allowed: FALLBACK_SOURCE_KINDS });
    expect(w.find('[data-testid="source-hint"]').text()).toContain('每一關啟動時');
    w.unmount();
  });

  it('only the list the source needs is requested', async () => {
    const m = await mountEditor({ source: 'manager', level: 1 });
    expect(api.getUsers).not.toHaveBeenCalled();
    expect(api.getGroups).not.toHaveBeenCalled();
    m.unmount();
    const u = await mountEditor({ source: 'users', userIds: [] });
    expect(api.getUsers).toHaveBeenCalled();
    expect(api.getGroups).not.toHaveBeenCalled();
    u.unmount();
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

  describe('unmatched groups left by opening a package', () => {
    const body = (id: string) => document.body.querySelector<HTMLElement>(`[data-testid="${id}"]`);
    const skipped: ApproverSource = { source: 'group', groupIds: [3], unmatchedGroups: ['採購組', '財務組'], candidatesUnmatched: false };

    it('shows the unmatched notice with group names', async () => {
      const w = await mountEditor(skipped);
      expect(w.find('[data-testid="source-unmatched-groups"]').text()).toBe('開啟套件時略過的群組：採購組、財務組。執行時由備援簽核人頂替，會簽仍要湊滿這些席位。');
      expect(w.find('[data-testid="source-unmatched-candidates"]').exists()).toBe(false);
      w.unmount();
    });

    it('shows the candidates notice', async () => {
      const w = await mountEditor({ source: 'userField', fieldId: 9, candidateGroupIds: [], candidatesUnmatched: true });
      expect(w.find('[data-testid="source-unmatched-candidates"]').text()).toBe('開啟套件時略過了候選群組，這個來源執行時一律由備援簽核人頂替。');
      expect(w.find('[data-testid="source-unmatched-groups"]').exists()).toBe(false);
      w.unmount();
    });

    it('no notice when nothing is unmatched', async () => {
      for (const s of [{ source: 'group', groupIds: [3] }, { source: 'group', groupIds: [3], unmatchedGroups: [], candidatesUnmatched: false }, { source: 'manager', level: 1, unmatchedGroups: null }] as ApproverSource[]) {
        const w = await mountEditor(s);
        expect(w.find('[data-testid="source-unmatched"]').exists()).toBe(false);
        w.unmount();
      }
    });

    it('remove asks for confirmation and clears both flags', async () => {
      const w = await mountEditor({ ...skipped, candidatesUnmatched: true });
      expect(w.find('[data-testid="source-unmatched-remove"]').attributes('aria-label')).toBe('移除開啟套件時略過的群組');
      await w.find('[data-testid="source-unmatched-remove"]').trigger('click');
      await flushPromises();
      expect(w.emitted('update:modelValue')).toBeUndefined();
      expect(body('source-unmatched-confirm-text')!.textContent).toBe('移除後這一關少了這些席位，確定要移除嗎？移除後，這個來源只能從目前選的候選群組選人，不再由備援簽核人頂替，確定要移除嗎？');
      body('source-unmatched-confirm')!.click();
      await flushPromises();
      expect(lastValue(w)).toEqual({ source: 'group', groupIds: [3], unmatchedGroups: null, candidatesUnmatched: false });
      w.unmount();
    });

    it('cancel keeps them', async () => {
      const w = await mountEditor(skipped);
      await w.find('[data-testid="source-unmatched-remove"]').trigger('click');
      await flushPromises();
      body('source-unmatched-cancel')!.click();
      await flushPromises();
      expect(w.emitted('update:modelValue')).toBeUndefined();
      expect(w.findComponent(quasar.QDialog).props('modelValue')).toBe(false);
      expect(w.find('[data-testid="source-unmatched"]').exists()).toBe(true);
      w.unmount();
    });

    it('a disabled editor cannot remove', async () => {
      const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
      api.getUsers.mockResolvedValue([] as never);
      api.getGroups.mockResolvedValue([]);
      const w = mount(ApproverSourceEditor, {
        attachTo: document.body,
        props: { modelValue: skipped, module: mod, allowed: ALL_SOURCE_KINDS, limits: {}, label: '來源', disable: true },
        global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], createPinia(), i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
      });
      await flushPromises();
      expect(w.find('[data-testid="source-unmatched-remove"]').attributes('disabled')).toBeDefined();
      w.unmount();
    });

    it('candidates confirm says only the selected candidate groups remain, not that seats are lost', async () => {
      const w = await mountEditor({ source: 'userField', fieldId: 9, candidateGroupIds: [3], candidatesUnmatched: true });
      await w.find('[data-testid="source-unmatched-remove"]').trigger('click');
      await flushPromises();
      expect(body('source-unmatched-confirm-text')!.textContent).toBe('移除後，這個來源只能從目前選的候選群組選人，不再由備援簽核人頂替，確定要移除嗎？');
      body('source-unmatched-confirm')!.click();
      await flushPromises();
      expect(lastValue(w)).toEqual({ source: 'userField', fieldId: 9, candidateGroupIds: [3], unmatchedGroups: null, candidatesUnmatched: false });
      w.unmount();
    });

    it('a pick source with every candidate skipped cannot drop the marker until candidates are chosen', async () => {
      const w = await mountEditor({ source: 'initiatorPick', candidateGroupIds: [], min: 1, max: 1, candidatesUnmatched: true });
      await w.find('[data-testid="source-unmatched-remove"]').trigger('click');
      await flushPromises();
      expect(body('source-unmatched-blocked-text')!.textContent).toContain('任何人都能被選為簽核人');
      expect(body('source-unmatched-confirm')).toBeNull();
      body('source-unmatched-cancel')!.click();
      await flushPromises();
      expect(w.emitted('update:modelValue')).toBeUndefined();
      w.unmount();
      // with a candidate group chosen the marker can go
      const ok = await mountEditor({ source: 'initiatorPick', candidateGroupIds: [3], min: 1, max: 1, candidatesUnmatched: true });
      await ok.find('[data-testid="source-unmatched-remove"]').trigger('click');
      await flushPromises();
      expect(body('source-unmatched-confirm')).not.toBeNull();
      ok.unmount();
    });

    it('skipped candidates show the banner only, not the "pick at least one candidate group" error', async () => {
      const w = await mountEditor({ source: 'userField', fieldId: 9, candidateGroupIds: [], candidatesUnmatched: true });
      expect(w.find('[data-testid="source-unmatched-candidates"]').exists()).toBe(true);
      expect(w.find('[data-testid="source-candidates-error"]').exists()).toBe(false);
      w.unmount();
    });

    it('blank skipped names still show the notice with a placeholder name', async () => {
      const w = await mountEditor({ source: 'group', groupIds: [], unmatchedGroups: ['  '] });
      expect(w.find('[data-testid="source-unmatched-groups"]').text()).toContain('未命名群組');
      w.unmount();
    });

    it('long names are allowed to break so a 390 px dialog never scrolls sideways', async () => {
      const long = '很長很長的群組名稱'.repeat(6);
      const w = await mountEditor({ source: 'group', groupIds: [3], unmatchedGroups: [long] });
      expect(w.find('[data-testid="source-unmatched-groups"]').attributes('style')).toContain('overflow-wrap: anywhere');
      w.unmount();
    });

    // jsdom has no layout, so what can be measured is which layout the widget asks for: the button shares the row with the text from 600 px up, drops below on a phone.
    it.each([[390, false], [820, true], [1366, true], [1920, true]])('at %i px the remove button is %s on the text row', async (width, inline) => {
      window.innerWidth = width;
      window.dispatchEvent(new Event('resize'));
      await new Promise((r) => setTimeout(r, 100));
      const w = await mountEditor(skipped);
      const actions = w.find('.q-banner__actions');
      expect(actions.classes()).toContain(inline ? 'col-auto' : 'col-all');
      expect(w.find('[data-testid="source-unmatched-remove"]').exists()).toBe(true);
      w.unmount();
      window.innerWidth = 1024;
      window.dispatchEvent(new Event('resize'));
    });
  });
});
