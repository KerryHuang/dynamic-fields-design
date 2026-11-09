import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import type { ModuleDetail } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import IconSelect from 'src/presentation/dynamic/designer/IconSelect.vue';
import { dynamicApi } from 'src/services/api/dynamic-api';
import ModuleEditorPage from './ModuleEditorPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { getModule: vi.fn(), updateModule: vi.fn(), entities: vi.fn(), getPreviewSchema: vi.fn(), exportTemplate: vi.fn(), deleteCheck: vi.fn(), deleteTarget: vi.fn(), getMe: vi.fn(),
  getEvents: vi.fn(), getChecks: vi.fn(), getLedger: vi.fn(), getWorkflow: vi.fn(), getModules: vi.fn() } }));
const api = vi.mocked(dynamicApi);

const detail: ModuleDetail = {
  id: 1, key: 'm', programCode: 'DYN00001', name: { 'zh-TW': '模組' }, section: 's', titleFieldId: 5,
  status: 'Active', version: 3, blocks: [],
};

async function mountPage(d: ModuleDetail = detail, canDelete = true, canRead = true) {
  api.getModule.mockResolvedValue(d);
  api.getMe.mockResolvedValue({ designer: { read: canRead, add: true, edit: true, delete: canDelete } } as never);
  api.entities.mockResolvedValue([{ key: 'test.entity', name: { 'zh-TW': '測試實體' } }]);
  api.updateModule.mockResolvedValue({} as never);
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/m/:moduleKey', component: ModuleEditorPage }] });
  await router.push('/m/m');
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const Host = { render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(ModuleEditorPage))) };
  const w = mount(Host, {
    global: {
      plugins: [createPinia(), [quasar.Quasar, { components: quasar, plugins: { Notify: quasar.Notify } }], router, i18n,
        [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]],
    },
  });
  await flushPromises();
  return w;
}

describe('ModuleEditorPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  it('picking an icon saves it and keeps the other module settings', async () => {
    const w = await mountPage();
    w.getComponent(IconSelect).vm.$emit('update:modelValue', 'factory');
    await flushPromises();
    expect(api.updateModule).toHaveBeenCalledWith('m', { name: detail.name, icon: 'factory', section: 's', titleFieldId: 5 });
  });

  it('saves the menu section with the module settings; emptying it clears the section', async () => {
    const w = await mountPage();
    const input = w.find('input[data-testid="module-section"]');
    expect((input.element as HTMLInputElement).value).toBe('s');
    expect(input.attributes('maxlength')).toBe('100');
    await input.setValue('  銷售 ');
    await input.trigger('change');
    await flushPromises();
    expect(api.updateModule).toHaveBeenLastCalledWith('m', { name: detail.name, icon: undefined, section: '銷售', titleFieldId: 5 });
    await input.setValue('  ');
    await input.trigger('change');
    await flushPromises();
    expect(api.updateModule).toHaveBeenLastCalledWith('m', { name: detail.name, icon: undefined, section: undefined, titleFieldId: 5 });
  });

  it('has tabs for linked events, cross-record checks and the ledger, each showing its own panel', async () => {
    api.getEvents.mockResolvedValue([]);
    api.getChecks.mockResolvedValue([]);
    api.getLedger.mockRejectedValue({ status: 404 });
    api.getWorkflow.mockRejectedValue({ status: 404 });
    api.getModules.mockResolvedValue([]);
    const w = await mountPage();
    for (const [name, panel] of [['events', 'events-tab'], ['checks', 'checks-tab'], ['ledger', 'ledger-tab']] as const) {
      const tab = w.find(`[data-testid="tab-${name}"]`);
      expect(tab.exists()).toBe(true);
      await tab.trigger('click');
      await flushPromises();
      expect(w.find(`[data-testid="${panel}"]`).exists()).toBe(true);
    }
    expect(w.find('[data-testid="tab-events"]').text()).toContain(zhTW.events.tab);
    expect(api.getEvents).toHaveBeenCalledWith('m');
    expect(api.getChecks).toHaveBeenCalledWith('m');
  });

  it('hides those tabs from someone without designer access', async () => {
    const w = await mountPage(detail, true, false);
    expect(w.find('[data-testid="tab-events"]').exists()).toBe(false);
    expect(w.find('[data-testid="tab-checks"]').exists()).toBe(false);
    expect(w.find('[data-testid="tab-ledger"]').exists()).toBe(false);
  });

  it('has a preview tab', async () => {
    api.getPreviewSchema.mockResolvedValue({ key: 'm', blocks: [], rules: [], permissions: {} } as never);
    const w = await mountPage();
    const tab = w.find('[data-testid="tab-preview"]');
    expect(tab.exists()).toBe(true);
    expect(tab.text()).toContain('預覽');
    await tab.trigger('click');
    await flushPromises();
    expect(api.getPreviewSchema).toHaveBeenCalledWith('m');
    expect(w.text()).toContain('預覽使用目前的草稿設定，不會存檔');
  });

  it('extension module hides the title field and shows the host entity read-only', async () => {
    const w = await mountPage({ ...detail, titleFieldId: undefined, kind: 'extension', hostEntityKey: 'test.entity' });
    expect(w.find('[data-testid="title-field"]').exists()).toBe(false);
    const host = w.find('input[data-testid="host-entity"]').element as HTMLInputElement;
    expect(host.value).toBe('測試實體');
    expect(host.readOnly).toBe(true);
  });

  it('export template downloads the file and revokes the url after 1000 ms', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout'] });
    const w = await mountPage();
    const blob = new Blob(['{}']);
    api.exportTemplate.mockResolvedValue({ blob, fileName: 'm.template.json' });
    URL.createObjectURL = vi.fn(() => 'blob:x');
    URL.revokeObjectURL = vi.fn();
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    (w.get('[data-testid="export-template"]').element as HTMLElement).click();
    await vi.waitFor(() => expect(click).toHaveBeenCalled());
    expect(api.exportTemplate).toHaveBeenCalledWith('m');
    expect(URL.revokeObjectURL).not.toHaveBeenCalled();
    vi.advanceTimersByTime(1000);
    expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:x');
    vi.useRealTimers();
    click.mockRestore();
  });
  describe('title template', () => {
    const f = (id: number, type: 'text' | 'integer' | 'enum', label: string) => ({
      id, key: `f_${id}`, type, label: { 'zh-TW': label }, blockId: 9, sequence: id, status: 'Active' as const, options: [],
      config: { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false },
    });
    const withFields = (titleTemplate?: string): ModuleDetail => ({
      ...detail, titleTemplate,
      blocks: [{ id: 9, label: { 'zh-TW': '區塊' }, sequence: 1, collapsed: false, columns: 2,
        fields: [f(5, 'text', '名稱'), f(6, 'text', '編號'), f(7, 'integer', '數量'), f(8, 'enum', '種類')] }],
    });
    const input = (w: Awaited<ReturnType<typeof mountPage>>) => w.find('input[data-testid="title-template"]');

    it('shows the template and a preview with field labels; other saves keep it', async () => {
      const w = await mountPage(withFields('{6} {5}'));
      expect((input(w).element as HTMLInputElement).value).toBe('{6} {5}');
      expect(w.find('[data-testid="title-template-field"]').text()).toContain('標題會像：編號 名稱');
      w.getComponent(IconSelect).vm.$emit('update:modelValue', 'factory');
      await flushPromises();
      expect(api.updateModule).toHaveBeenLastCalledWith('m', { name: detail.name, icon: 'factory', section: 's', titleFieldId: 5, titleTemplate: '{6} {5}' });
    });

    it('saves on change; blank clears it', async () => {
      const w = await mountPage(withFields());
      expect(w.find('[data-testid="title-template-field"]').text()).toContain(zhTW.designer.titleTemplateHint);
      await input(w).setValue(' {6}-{5} ');
      await input(w).trigger('change');
      await flushPromises();
      expect(api.updateModule).toHaveBeenLastCalledWith('m', { name: detail.name, icon: undefined, section: 's', titleFieldId: 5, titleTemplate: '{6}-{5}' });
      const w2 = await mountPage(withFields('{6}'));
      await input(w2).setValue('  ');
      await input(w2).trigger('change');
      await flushPromises();
      expect(api.updateModule).toHaveBeenLastCalledWith('m', { name: detail.name, icon: undefined, section: 's', titleFieldId: 5, titleTemplate: undefined });
    });

    it('insert field offers only text-like fields and adds {id} to the template', async () => {
      const w = await mountPage(withFields('{6}'));
      const el = w.get('[data-testid="title-template-insert"]').element;
      const select = w.findAllComponents(quasar.QSelect).find((c) => c.element.contains(el))!;
      const options = (select.props('options') as { value: number }[]).map((o) => o.value);
      expect(options).toEqual([5, 6, 8]);
      select.vm.$emit('update:modelValue', 5);
      await flushPromises();
      expect(api.updateModule).toHaveBeenLastCalledWith('m', { name: detail.name, icon: undefined, section: 's', titleFieldId: 5, titleTemplate: '{6} {5}' });
    });

    it('shows the server error under the template', async () => {
      const w = await mountPage(withFields());
      api.updateModule.mockRejectedValue({ status: 400, errors: { TitleTemplate: ['標題樣板的大括號沒有成對'] } });
      await input(w).setValue('{6');
      await input(w).trigger('change');
      await vi.waitFor(() => expect(w.find('[data-testid="title-template-field"]').text()).toContain('標題樣板的大括號沒有成對'));
    });

    it('an extension module has no title template', async () => {
      const w = await mountPage({ ...withFields(), titleFieldId: undefined, kind: 'extension', hostEntityKey: 'test.entity' });
      expect(input(w).exists()).toBe(false);
    });
  });

  describe('delete entry points', () => {
    const field = (id: number, status: 'Active' | 'Inactive') => ({
      id, key: `f_${id}`, type: 'text' as const, label: { 'zh-TW': `欄${id}` }, blockId: 9, sequence: id, status, options: [],
      config: { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false },
    });
    const withBlock = (status: ModuleDetail['status']): ModuleDetail => ({
      ...detail, status, blocks: [{ id: 9, label: { 'zh-TW': '區塊' }, sequence: 1, collapsed: false, columns: 2, fields: [field(1, 'Active'), field(2, 'Inactive')] }],
    });
    const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

    it('an active module has no delete-module button; a draft or inactive one does', async () => {
      expect((await mountPage(withBlock('Active'))).find('[data-testid="delete-module"]').exists()).toBe(false);
      expect((await mountPage(withBlock('Inactive'))).find('[data-testid="delete-module"]').exists()).toBe(true);
      expect((await mountPage(withBlock('Draft'))).find('[data-testid="delete-module"]').exists()).toBe(true);
    });

    it('without the designer Delete permission no delete entry is shown at all', async () => {
      const w = await mountPage(withBlock('Draft'), false);
      expect(w.find('[data-testid="delete-module"]').exists()).toBe(false);
      expect(w.find('[data-testid="delete-block-9"]').exists()).toBe(false);
      expect(w.find('[data-testid="delete-field-2"]').exists()).toBe(false);
    });

    it('only inactive fields get a delete button; clicking it runs the field delete check', async () => {
      api.deleteCheck.mockResolvedValue({ canDelete: true, blockers: [], purge: { records: 0, indexRows: 0, historyRows: 0 } });
      const w = await mountPage(withBlock('Draft'));
      expect(w.find('[data-testid="delete-field-1"]').exists()).toBe(false);
      await w.get('[data-testid="delete-field-2"]').trigger('click');
      await flushPromises();
      expect(api.deleteCheck).toHaveBeenCalledWith({ kind: 'field', id: 2 });
      expect(q('delete-dialog')).not.toBeNull();
      w.unmount();
    });

    it('the block row delete button runs the block delete check; the module button the module one', async () => {
      api.deleteCheck.mockResolvedValue({ canDelete: false, blockers: [], purge: { records: 0, indexRows: 0, historyRows: 0 } });
      const w = await mountPage(withBlock('Draft'));
      await w.get('[data-testid="delete-block-9"]').trigger('click');
      await flushPromises();
      expect(api.deleteCheck).toHaveBeenCalledWith({ kind: 'block', id: 9 });
      w.unmount();
      const w2 = await mountPage(withBlock('Draft'));
      await w2.get('[data-testid="delete-module"]').trigger('click');
      await flushPromises();
      expect(api.deleteCheck).toHaveBeenCalledWith({ kind: 'module', key: 'm' });
      w2.unmount();
    });
  });
});
