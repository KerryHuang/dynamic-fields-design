import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { ModuleCheck } from 'src/domain/dynamic/events-ledger';
import type { DesignerField, ModuleDetail } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import ChecksTab from './ChecksTab.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: { getChecks: vi.fn(), deleteModuleCheck: vi.fn(), getMe: vi.fn(), getModule: vi.fn() },
}));
const api = vi.mocked(dynamicApi);

const f = (id: number, type: DesignerField['type']): DesignerField => ({
  id, key: `f_${id}`, type, label: { 'zh-TW': `欄${id}` }, blockId: 1, sequence: id, status: 'Active', options: [],
  config: { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false },
});
const mod: ModuleDetail = {
  id: 1, key: 'move', programCode: 'P', name: { 'zh-TW': '異動' }, status: 'Active', version: 1,
  blocks: [{ id: 1, label: { 'zh-TW': 'b' }, sequence: 1, collapsed: false, columns: 2, fields: [f(51, 'text'), f(55, 'integer')] }],
};
const check: ModuleCheck = {
  key: 'stock', name: { 'zh-TW': '庫存不得為負' }, moduleKey: 'move', active: true,
  definition: { groupBy: [51], function: 'sum', fieldId: 55, assert: { op: 'gte', limit: { const: 0 } }, message: { 'zh-TW': '庫存不足' } },
};

async function mountTab(checks: ModuleCheck[], canEdit = true) {
  api.getChecks.mockResolvedValue(checks);
  api.getMe.mockResolvedValue({ designer: { read: true, add: true, edit: canEdit, delete: true } } as never);
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(ChecksTab, {
    props: { module: mod },
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], createPinia(), i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
    attachTo: document.body,
  });
  await flushPromises();
  return w;
}
const $ = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

describe('ChecksTab', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    document.querySelectorAll('.q-dialog').forEach((el) => el.remove());
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  it('lists the module checks with a readable rule', async () => {
    const w = await mountTab([check]);
    expect(api.getChecks).toHaveBeenCalledWith('move');
    const row = w.find('[data-testid="check-row-stock"]').text();
    expect(row).toContain('庫存不得為負');
    expect(row).toContain('加總(欄55) ≥ 0');
  });

  it('says so when there is nothing yet, and opens the dialog to add one', async () => {
    const w = await mountTab([]);
    expect(w.text()).toContain(zhTW.checks.empty);
    await w.find('[data-testid="check-create"]').trigger('click');
    await flushPromises();
    expect($('check-save')).not.toBeNull();
  });

  it('deletes a check after confirming', async () => {
    api.deleteModuleCheck.mockResolvedValue(undefined as never);
    const w = await mountTab([check]);
    await w.find('[data-testid="check-delete-stock"]').trigger('click');
    await flushPromises();
    ($('check-delete-confirm') as HTMLElement).click();
    await flushPromises();
    expect(api.deleteModuleCheck).toHaveBeenCalledWith('stock');
  });

  it('without designer edit permission the buttons are disabled', async () => {
    const w = await mountTab([check], false);
    expect(w.find('[data-testid="check-create"]').attributes('disabled')).toBeDefined();
    expect(w.find('[data-testid="check-delete-stock"]').attributes('disabled')).toBeDefined();
  });
});
