import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { DesignerField, ModuleDetail, Workflow } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import WorkflowTab from './WorkflowTab.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: { getWorkflow: vi.fn(), putWorkflow: vi.fn(), deleteWorkflow: vi.fn(), getGroups: vi.fn(), getMe: vi.fn() },
}));
const api = vi.mocked(dynamicApi);

const status: DesignerField = {
  id: 5, key: 'f_5', type: 'enum', label: { 'zh-TW': '狀態' }, blockId: 1, sequence: 1, status: 'Active',
  config: { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false },
  options: [11, 12, 13].map((n) => ({ id: n, value: `opt_${n}`, label: { 'zh-TW': `狀態${n}` }, sequence: n, active: n !== 13 })),
};
const mod = (extra: Partial<ModuleDetail> = {}): ModuleDetail => ({
  id: 1, key: 'doc', programCode: 'P', name: { 'zh-TW': '單據' }, status: 'Active', version: 1,
  blocks: [{ id: 1, label: { 'zh-TW': 'b' }, sequence: 1, collapsed: false, columns: 2, fields: [status] }], ...extra,
});
const saved: Workflow = {
  statusFieldId: 5, initialOptionId: 11,
  states: [{ optionId: 11, readOnly: false }, { optionId: 12, readOnly: true }],
  transitions: [{ from: 11, to: 12, groupIds: [3] }],
};

async function mountTab(m: ModuleDetail, existing: Workflow | null) {
  api.getWorkflow.mockImplementation(async () => {
    if (existing) return existing;
    throw { status: 404 };
  });
  api.getGroups.mockResolvedValue([{ id: 3, key: 'mgr', name: { 'zh-TW': '主管' }, members: [] }]);
  api.getMe.mockResolvedValue({ designer: { read: true, add: true, edit: true, delete: true } } as never);
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(WorkflowTab, {
    props: { module: m },
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], createPinia(), i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
  });
  await flushPromises();
  return w;
}

describe('WorkflowTab', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  it('loads a saved workflow into the form and saves it back unchanged', async () => {
    api.putWorkflow.mockResolvedValue(saved);
    const w = await mountTab(mod(), saved);
    expect((w.find('[data-testid="state-included-11"]').element as HTMLElement).getAttribute('aria-checked')).toBe('true');
    expect(w.find('[data-testid="state-row-13"]').exists()).toBe(false); // inactive options are not offered
    await w.find('[data-testid="save-workflow"]').trigger('click');
    await flushPromises();
    expect(api.putWorkflow).toHaveBeenCalledWith('doc', saved);
  });

  it('cannot be saved until a status field and an initial state are picked', async () => {
    const w = await mountTab(mod(), null);
    expect(w.find('[data-testid="save-workflow"]').attributes('disabled')).toBeDefined();
    expect(w.find('[data-testid="remove-workflow"]').exists()).toBe(false);
  });

  it('shows the server messages when saving fails', async () => {
    api.putWorkflow.mockRejectedValue({ status: 400, errors: { transitions: ['轉移的狀態不在狀態清單內'] } });
    const w = await mountTab(mod(), saved);
    await w.find('[data-testid="save-workflow"]').trigger('click');
    await flushPromises();
    expect(w.find('[data-testid="workflow-errors"]').text()).toContain('轉移的狀態不在狀態清單內');
  });

  it('removes the workflow after confirming', async () => {
    api.deleteWorkflow.mockResolvedValue(undefined as never);
    const w = await mountTab(mod(), saved);
    await w.find('[data-testid="remove-workflow"]').trigger('click');
    await flushPromises();
    (document.body.querySelector('[data-testid="confirm-remove-workflow"]') as HTMLElement).click();
    await flushPromises();
    expect(api.deleteWorkflow).toHaveBeenCalledWith('doc');
    w.unmount();
  });

  it('explains that detail modules cannot have a workflow', async () => {
    const w = await mountTab(mod({ detailOf: 'header' }), null);
    expect(w.find('[data-testid="workflow-unsupported"]').exists()).toBe(true);
    expect(w.find('[data-testid="save-workflow"]').exists()).toBe(false);
  });
});
