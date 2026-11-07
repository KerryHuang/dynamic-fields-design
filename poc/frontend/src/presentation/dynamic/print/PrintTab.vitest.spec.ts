import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { DesignerField, ModuleDetail, PrintTemplateDefinition } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import PrintTab from './PrintTab.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: {
    getPrintTemplateDefinitions: vi.fn(), createPrintTemplate: vi.fn(), updatePrintTemplate: vi.fn(),
    deletePrintTemplate: vi.fn(), getMe: vi.fn(),
  },
}));
const api = vi.mocked(dynamicApi);

const field: DesignerField = {
  id: 7, key: 'f_7', type: 'text', label: { 'zh-TW': '客戶' }, blockId: 1, sequence: 1, status: 'Active',
  config: { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false }, options: [],
};
const mod = (extra: Partial<ModuleDetail> = {}): ModuleDetail => ({
  id: 1, key: 'quote', programCode: 'P', name: { 'zh-TW': '報價' }, status: 'Active', version: 1,
  blocks: [{ id: 1, label: { 'zh-TW': 'b' }, sequence: 1, collapsed: false, columns: 2, fields: [field] }], ...extra,
});
const tpl: PrintTemplateDefinition = { key: 'sheet', name: { 'zh-TW': '報價單' }, body: '<p>{{f_7}}</p>', active: true };

async function mountTab(m: ModuleDetail, canEdit = true) {
  api.getPrintTemplateDefinitions.mockResolvedValue([tpl]);
  api.getMe.mockResolvedValue({ designer: { read: true, add: canEdit, edit: canEdit, delete: canEdit } } as never);
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(PrintTab, {
    props: { module: m },
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], createPinia(), i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
  });
  await flushPromises();
  return w;
}

describe('PrintTab', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  it('lists templates and saves an edited one under its fixed key', async () => {
    api.updatePrintTemplate.mockResolvedValue(tpl);
    const w = await mountTab(mod());
    await w.find('[data-testid="print-item-sheet"]').trigger('click');
    expect((w.find('[data-testid="print-key"]').element as HTMLInputElement).disabled).toBe(true);
    await w.find('[data-testid="print-insert-7"]').trigger('click');
    await w.find('[data-testid="print-save"]').trigger('click');
    await flushPromises();
    const [moduleKey, sent] = api.updatePrintTemplate.mock.calls[0]!;
    expect(moduleKey).toBe('quote');
    expect(sent.key).toBe('sheet');
    expect(sent.body).toContain('{{f_7}}');
  });

  it('creates a new template and shows the server messages when it is rejected', async () => {
    api.createPrintTemplate.mockRejectedValue({ status: 400, errors: { body: ['{{#each}} 缺少結尾的 {{/each}}'] } });
    const w = await mountTab(mod());
    await w.find('[data-testid="print-add"]').trigger('click');
    await w.find('[data-testid="print-key"]').setValue('new-one');
    await w.find('[data-testid="print-save"]').trigger('click');
    await flushPromises();
    expect(api.createPrintTemplate).toHaveBeenCalled();
    expect(w.find('[data-testid="print-errors"]').text()).toContain('缺少結尾');
  });

  it('is read-only without the designer edit privilege', async () => {
    const w = await mountTab(mod(), false);
    expect(w.find('[data-testid="print-add"]').exists()).toBe(false);
    await w.find('[data-testid="print-item-sheet"]').trigger('click');
    expect(w.find('[data-testid="print-save"]').exists()).toBe(false);
  });

  it('explains that detail modules have no templates of their own', async () => {
    const w = await mountTab(mod({ detailOf: 'order' }));
    expect(w.find('[data-testid="print-unsupported"]').exists()).toBe(true);
  });
});
