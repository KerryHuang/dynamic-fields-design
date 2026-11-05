import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { ModuleSchema } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import DynamicForm from 'src/presentation/dynamic/components/DynamicForm.vue';
import PreviewTab from './PreviewTab.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: { getPreviewSchema: vi.fn(), createRecord: vi.fn(), updateRecord: vi.fn() },
}));
const api = vi.mocked(dynamicApi);

const text = (l: string) => ({ 'zh-TW': l });
const schema = (label: string): ModuleSchema => ({
  key: 'm', programCode: 'DYN_M', name: text('模組'), titleFieldKey: 'f_1', version: 1, rules: [],
  permissions: { read: true, add: true, edit: true, delete: true, printExport: true, pricing: true },
  blocks: [{ id: 1, label: text('區'), collapsed: false, fields: [{
    id: 1, key: 'f_1', type: 'text' as const, label: text(label), options: [],
    config: { required: false, readOnly: false, showInList: true, isFilterable: true, isSortable: true },
  }] }],
});

function mountTab(version: number) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  return mount(PreviewTab, {
    props: { moduleKey: 'm', version },
    global: {
      plugins: [[quasar.Quasar, { components: quasar, plugins: { Notify: quasar.Notify } }], i18n,
        [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]],
    },
  });
}

describe('PreviewTab', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
    api.getPreviewSchema.mockResolvedValue(schema('欄位甲'));
  });

  it('renders DynamicForm in create mode from the preview schema with the draft notice', async () => {
    const w = mountTab(1);
    await flushPromises();
    expect(api.getPreviewSchema).toHaveBeenCalledWith('m');
    expect(w.text()).toContain('預覽使用目前的草稿設定，不會存檔');
    const form = w.getComponent(DynamicForm);
    expect(form.props('mode')).toBe('create');
    expect(form.props('schema').blocks[0].fields[0].key).toBe('f_1');
  });

  it('does not call any write API on save and notifies "預覽不會存檔"', async () => {
    const w = mountTab(1);
    await flushPromises();
    w.getComponent(DynamicForm).vm.$emit('submit', {}, {});
    await flushPromises();
    expect(document.body.textContent).toContain('預覽不會存檔');
    expect(api.createRecord).not.toHaveBeenCalled();
    expect(api.updateRecord).not.toHaveBeenCalled();
  });

  it('refetches when the module version changes', async () => {
    const w = mountTab(1);
    await flushPromises();
    expect(api.getPreviewSchema).toHaveBeenCalledTimes(1);
    api.getPreviewSchema.mockResolvedValue(schema('欄位乙'));
    await w.setProps({ version: 2 });
    await flushPromises();
    expect(api.getPreviewSchema).toHaveBeenCalledTimes(2);
    expect(w.getComponent(DynamicForm).props('schema').blocks[0].fields[0].label['zh-TW']).toBe('欄位乙');
  });
});
