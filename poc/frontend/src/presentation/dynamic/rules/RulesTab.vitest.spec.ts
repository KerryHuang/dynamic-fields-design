import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { createPinia } from 'pinia';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { DesignerField, Me, ModuleDetail, Rule } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import RulesTab from './RulesTab.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: { getRules: vi.fn(), putRules: vi.fn(), getMe: vi.fn() },
}));
const api = vi.mocked(dynamicApi);

const fld = (id: number, label: string): DesignerField => ({
  id, key: `f_${id}`, type: 'integer', label: { 'zh-TW': label }, blockId: 1, sequence: id, status: 'Active', options: [],
  config: { required: false, readOnly: false, showInList: true, isFilterable: false, isSortable: false },
});
const mod: ModuleDetail = {
  id: 1, key: 'a', programCode: 'DYN_A', name: { 'zh-TW': 'A' }, status: 'Active', version: 1,
  blocks: [{ id: 1, label: { 'zh-TW': '區塊' }, sequence: 1, collapsed: false, fields: [fld(1, '數量'), fld(2, '上限')] }],
};
const rules: Rule[] = [
  { id: 1, kind: 'required', condition: { all: [{ field: 1, op: 'gt', value: 5 }] }, targets: ['field:2'], optionIds: [], active: true },
  { id: 2, kind: 'readOnly', condition: { all: [] }, targets: ['*'], optionIds: [], active: true },
];
const me = (edit: boolean): Me => ({
  userId: 'a', displayName: 'A', isSystemAdmin: false, groups: [], designer: { read: true, add: true, edit, delete: false },
});

function mountTab(canEdit = true) {
  api.getRules.mockResolvedValue(JSON.parse(JSON.stringify(rules)));
  api.getMe.mockResolvedValue(me(canEdit));
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  return mount(RulesTab, {
    props: { module: mod },
    global: {
      plugins: [[quasar.Quasar, { components: quasar, plugins: { Notify: quasar.Notify } }], i18n, createPinia(),
        [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]],
    },
  });
}
const id = (w: ReturnType<typeof mountTab>, t: string) => w.find(`[data-testid="${t}"]`);

describe('RulesTab', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  it('lists kind, condition summary and targets per row', async () => {
    const w = mountTab();
    await flushPromises();
    const row0 = id(w, 'rule-row-0').text();
    expect(row0).toContain('必填');
    expect(row0).toContain('數量');
    expect(row0).toContain('大於');
    expect(row0).toContain('上限');
    expect(id(w, 'rule-row-1').text()).toContain('整筆記錄');
  });

  it('PUTs the whole list after move, deactivate and delete', async () => {
    const w = mountTab();
    await flushPromises();
    await id(w, 'rule-up-1').trigger('click');
    await id(w, 'rule-active-0').trigger('click'); // now the readOnly rule
    await id(w, 'rule-delete-1').trigger('click'); // the required rule
    api.putRules.mockResolvedValue([]);
    await id(w, 'save-rules').trigger('click');
    await flushPromises();
    const [key, body] = api.putRules.mock.calls[0]!;
    expect(key).toBe('a');
    expect(body).toEqual([{ ...rules[1], active: false }]);
    await vi.waitFor(() => expect(document.body.textContent).toContain(zhTW.designer.rules.saved));
  });

  it('marks the row named by rules[1].targets and shows the message', async () => {
    const w = mountTab();
    await flushPromises();
    api.putRules.mockRejectedValue({ status: 400, errors: { 'rules[1].targets': ['規則目標不正確'] } });
    await id(w, 'save-rules').trigger('click');
    await flushPromises();
    expect(id(w, 'rule-error-0').exists()).toBe(false);
    expect(id(w, 'rule-error-1').text()).toBe('規則目標不正確');
    expect(id(w, 'rule-row-1').classes()).toContain('rule-row--error');
  });

  it('is read-only without designer.edit', async () => {
    const w = mountTab(false);
    await flushPromises();
    for (const t of ['add-rule', 'save-rules', 'rule-edit-0', 'rule-delete-0', 'rule-up-1', 'rule-active-0']) {
      expect(id(w, t).attributes('disabled'), t).toBeDefined();
    }
  });
});
