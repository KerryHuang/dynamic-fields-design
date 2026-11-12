import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import type { Conversion, DesignerField, FieldType, ModuleDetail } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import ConversionsPage from './ConversionsPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: {
    getConversions: vi.fn(), updateConversion: vi.fn(), createConversion: vi.fn(), deleteConversion: vi.fn(), getModules: vi.fn(),
    getModule: vi.fn(), getWorkflow: vi.fn(), getMe: vi.fn(),
  },
}));
const api = vi.mocked(dynamicApi);

const base = { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false };
const mk = (id: number, type: FieldType, label: string, config: Partial<DesignerField['config']> = {}): DesignerField =>
  ({ id, key: `f_${id}`, type, label: { 'zh-TW': label }, blockId: 1, sequence: id, status: 'Active', options: [], config: { ...base, ...config } });
const moduleOf = (key: string, fields: DesignerField[]): ModuleDetail => ({
  id: 1, key, programCode: 'P', name: { 'zh-TW': key }, status: 'Active', version: 1,
  blocks: [{ id: 1, label: { 'zh-TW': 'b' }, sequence: 1, collapsed: false, columns: 2, fields }],
});
const details: Record<string, ModuleDetail> = {
  quote: moduleOf('quote', [mk(21, 'text', '客戶')]),
  order: moduleOf('order', [mk(71, 'text', '客戶'), mk(80, 'reference', '來源報價', { readOnly: true, refTarget: { kind: 'module', key: 'quote' } })]),
};
const conversion: Conversion = {
  key: 'q2o', name: { 'zh-TW': '報價轉訂單' }, sourceModuleKey: 'quote', targetModuleKey: 'order', active: true,
  definition: { sourceStates: [], header: { sourceFieldId: 80, copy: [{ from: 21, to: 71 }] } },
};

function mountPage() {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const Host = { render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(ConversionsPage))) };
  return mount(Host, {
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], createPinia(), i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
  });
}

describe('ConversionsPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
    api.getConversions.mockResolvedValue([conversion]);
    api.getModules.mockResolvedValue([
      { id: 1, key: 'quote', programCode: 'P', name: { 'zh-TW': '報價單' }, status: 'Active', version: 1, kind: 'standalone' },
      { id: 2, key: 'order', programCode: 'P', name: { 'zh-TW': '訂單' }, status: 'Active', version: 1, kind: 'standalone' },
    ] as never);
    api.getModule.mockImplementation(async (k) => details[k]!);
    api.getWorkflow.mockRejectedValue({ status: 404 });
    api.getMe.mockResolvedValue({ designer: { read: true, add: true, edit: true, delete: true } } as never);
  });
  afterEach(() => { document.body.innerHTML = ''; });

  it('lists the conversions with their source and target modules', async () => {
    const w = mountPage();
    await flushPromises();
    const row = w.find('[data-testid="conversion-row-q2o"]');
    expect(row.text()).toContain('報價轉訂單');
    expect(row.text()).toContain('報價單 → 訂單');
    w.unmount();
  });

  it('edits a conversion: the key is fixed, and saving PUTs the same definition back', async () => {
    api.updateConversion.mockResolvedValue(conversion);
    const w = mountPage();
    await flushPromises();
    await w.find('[data-testid="conversion-row-q2o"]').trigger('click');
    await flushPromises();
    expect((document.body.querySelector('[data-testid="conversion-key"]') as HTMLInputElement).readOnly).toBe(true);
    (document.body.querySelector('[data-testid="conversion-save"]') as HTMLElement).click();
    await flushPromises();
    expect(api.updateConversion).toHaveBeenCalledWith('q2o', conversion);
    w.unmount();
  });

  it('shows definition errors from the server in the dialog', async () => {
    api.updateConversion.mockRejectedValue({ status: 400, errors: { 'definition.header.copy[0].to': ['目標欄位型別不相容'] } });
    const w = mountPage();
    await flushPromises();
    await w.find('[data-testid="conversion-row-q2o"]').trigger('click');
    await flushPromises();
    (document.body.querySelector('[data-testid="conversion-save"]') as HTMLElement).click();
    await flushPromises();
    expect(document.body.querySelector('[data-testid="conversion-errors"]')!.textContent).toContain('目標欄位型別不相容');
    w.unmount();
  });

  it('the delete button does not open the editor', async () => {
    const w = mountPage();
    await flushPromises();
    expect(w.find('[data-testid="conversion-row-q2o"]').classes()).toContain('row-clickable');
    await w.find('[data-testid="conversion-delete-q2o"]').trigger('click');
    await flushPromises();
    expect(document.body.querySelector('[data-testid="conversion-key"]')).toBeNull();
    w.unmount();
  });

  it('keyboard: the row is focusable and Enter opens the editor', async () => {
    const w = mountPage();
    await flushPromises();
    const row = w.find('[data-testid="conversion-row-q2o"]');
    expect(row.attributes('tabindex')).toBe('0');
    await row.trigger('keydown', { key: 'Enter' });
    await flushPromises();
    expect(document.body.querySelector('[data-testid="conversion-key"]')).not.toBeNull();
    w.unmount();
  });

  it('without edit permission the row is not clickable and does not open the editor', async () => {
    api.getMe.mockResolvedValue({ designer: { read: true, add: false, edit: false, delete: false } } as never);
    const w = mountPage();
    await flushPromises();
    const row = w.find('[data-testid="conversion-row-q2o"]');
    expect(row.classes()).not.toContain('row-clickable');
    expect(row.attributes('tabindex')).toBeUndefined();
    await row.trigger('keydown', { key: 'Enter' });
    await row.trigger('click');
    await flushPromises();
    expect(document.body.querySelector('[data-testid="conversion-key"]')).toBeNull();
    w.unmount();
  });

  it('deletes after confirming', async () => {
    api.deleteConversion.mockResolvedValue(undefined as never);
    const w = mountPage();
    await flushPromises();
    await w.find('[data-testid="conversion-delete-q2o"]').trigger('click');
    await flushPromises();
    (document.body.querySelector('[data-testid="conversion-delete-confirm"]') as HTMLElement).click();
    await flushPromises();
    expect(api.deleteConversion).toHaveBeenCalledWith('q2o');
    w.unmount();
  });
});
