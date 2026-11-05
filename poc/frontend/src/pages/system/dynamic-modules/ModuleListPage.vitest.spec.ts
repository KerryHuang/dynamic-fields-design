import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia } from 'pinia';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import ModuleListPage from './ModuleListPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: { getModules: vi.fn(), createModule: vi.fn(), entities: vi.fn(), getMe: vi.fn(), importTemplate: vi.fn(), getTemplates: vi.fn() },
}));
const api = vi.mocked(dynamicApi);

async function mountPage(modules: unknown[] = [], add = true, edit = true) {
  api.getMe.mockResolvedValue({ designer: { read: true, add, edit, delete: true } } as never);
  api.getTemplates.mockResolvedValue([]);
  api.getModules.mockResolvedValue(modules as never);
  api.entities.mockResolvedValue([{ key: 'design.mold', name: { 'zh-TW': '模具' } }]);
  api.createModule.mockResolvedValue({} as never);
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/', component: ModuleListPage }, { path: '/system/dynamic-modules/:k', component: { render: () => null } }],
  });
  await router.push('/');
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const Host = { render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(ModuleListPage))) };
  const w = mount(Host, {
    attachTo: document.body,
    global: {
      plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], router, i18n, createPinia(),
        [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]],
    },
  });
  await flushPromises();
  return w;
}
const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`);
const button = (text: string) => [...document.body.querySelectorAll('button')].find((b) => b.textContent?.includes(text) && !b.dataset.testid?.includes('template'))!;

async function fillExtension(w: Awaited<ReturnType<typeof mountPage>>) {
  (q('new-module') as HTMLElement).click();
  await flushPromises();
  const key = document.body.querySelector('input:not([type=file])') as HTMLInputElement;
  key.value = 'mold-ext';
  key.dispatchEvent(new Event('input'));
  await w.findComponent({ name: 'I18nTextInput' }).vm.$emit('update:modelValue', { 'zh-TW': '模具擴充' });
  await w.findAllComponents({ name: 'QOptionGroup' })[0]!.vm.$emit('update:modelValue', 'extension');
  await flushPromises();
}

describe('ModuleListPage create dialog', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    document.body.innerHTML = '';
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  it('create dialog with extension kind requires host entity', async () => {
    const w = await mountPage();
    await fillExtension(w);
    expect(q('host-entity')).not.toBeNull();
    button('建立').click();
    await flushPromises();
    expect(api.createModule).not.toHaveBeenCalled();
    expect(q('host-entity')!.textContent).toContain('請選擇既有實體');
    w.unmount();
  });

  it('submits kind and hostEntityKey', async () => {
    const w = await mountPage();
    await fillExtension(w);
    await w.findAllComponents({ name: 'QSelect' }).find((c) => (c.vm.$el as HTMLElement).closest('[data-testid="host-entity"]'))!
      .vm.$emit('update:modelValue', 'design.mold');
    await flushPromises();
    button('建立').click();
    await flushPromises();
    expect(api.createModule).toHaveBeenCalledWith(expect.objectContaining({ key: 'mold-ext', kind: 'extension', hostEntityKey: 'design.mold' }));
    w.unmount();
  });

  it('standalone sends kind without hostEntityKey', async () => {
    const w = await mountPage();
    (q('new-module') as HTMLElement).click();
    await flushPromises();
    expect(q('host-entity')).toBeNull();
    const key = document.body.querySelector('input:not([type=file])') as HTMLInputElement;
    key.value = 'plain-mod';
    key.dispatchEvent(new Event('input'));
    await w.findComponent({ name: 'I18nTextInput' }).vm.$emit('update:modelValue', { 'zh-TW': '一般' });
    await flushPromises();
    button('建立').click();
    await flushPromises();
    const body = api.createModule.mock.calls[0]![0];
    expect(body.kind).toBe('standalone');
    expect(body).not.toHaveProperty('hostEntityKey');
    w.unmount();
  });

  it('sends section when creating a module; empty input sends no section', async () => {
    const fill = async (w: Awaited<ReturnType<typeof mountPage>>, key: string, section: string) => {
      (q('new-module') as HTMLElement).click();
      await flushPromises();
      const input = document.body.querySelector('input:not([type=file])') as HTMLInputElement;
      input.value = key;
      input.dispatchEvent(new Event('input'));
      await w.findComponent({ name: 'I18nTextInput' }).vm.$emit('update:modelValue', { 'zh-TW': '名' });
      const sec = document.body.querySelector('input[data-testid="module-section"]') as HTMLInputElement;
      expect(sec.getAttribute('maxlength')).toBe('100');
      sec.value = section;
      sec.dispatchEvent(new Event('input'));
      await flushPromises();
      button('建立').click();
      await flushPromises();
    };
    const w = await mountPage();
    await fill(w, 'with-sec', '  銷售  ');
    expect(api.createModule.mock.calls[0]![0].section).toBe('銷售');
    w.unmount();
    document.body.innerHTML = '';
    const w2 = await mountPage();
    await fill(w2, 'no-sec', '   ');
    expect(api.createModule.mock.calls[1]![0].section).toBeUndefined();
    w2.unmount();
  });

  it('switching back to standalone after picking a host drops hostEntityKey', async () => {
    const w = await mountPage();
    await fillExtension(w);
    await w.findAllComponents({ name: 'QSelect' }).find((c) => (c.vm.$el as HTMLElement).closest('[data-testid="host-entity"]'))!
      .vm.$emit('update:modelValue', 'design.mold');
    await w.findAllComponents({ name: 'QOptionGroup' })[0]!.vm.$emit('update:modelValue', 'standalone');
    await flushPromises();
    button('建立').click();
    await flushPromises();
    const body = api.createModule.mock.calls[0]![0];
    expect(body.kind).toBe('standalone');
    expect(body).not.toHaveProperty('hostEntityKey');
    w.unmount();
  });
});

describe('ModuleListPage detail marker', () => {
  it('detail module shows detailOf marker', async () => {
    document.body.innerHTML = '';
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
    const base = { programCode: 'P', status: 'Active', version: 1 };
    const w = await mountPage([
      { ...base, id: 1, key: 'order', name: { 'zh-TW': '訂單' } },
      { ...base, id: 2, key: 'order-line', name: { 'zh-TW': '訂單明細' }, detailOf: 'order' },
    ]);
    const text = (k: string) => document.body.querySelector(`[data-testid="detail-of-${k}"]`)?.textContent;
    expect(text('order-line')).toBe('明細：訂單');
    expect(text('order')).toBeUndefined();
    w.unmount();
  });
});

describe('ModuleListPage templates', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    document.body.innerHTML = '';
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });
  const pickFile = async (text: string) => {
    const input = q('import-template-input') as HTMLInputElement;
    const file = new File([text], 't.json', { type: 'application/json' });
    Object.defineProperty(input, 'files', { value: [file], configurable: true });
    input.dispatchEvent(new Event('change'));
    await flushPromises();
  };

  it('hides both template buttons without designer Add', async () => {
    const w = await mountPage([], false);
    expect(q('from-template')).toBeNull();
    expect(q('import-template')).toBeNull();
    w.unmount();
  });

  it('designer Add without Edit shows import only: from-template replays edit commands and would get 403', async () => {
    const w = await mountPage([], true, false);
    expect(q('import-template')).not.toBeNull();
    expect(q('from-template')).toBeNull();
    w.unmount();
  });

  it('shows them with designer Add and Edit, and from-template opens the dialog', async () => {
    const w = await mountPage();
    expect(q('import-template')).not.toBeNull();
    (q('from-template') as HTMLElement).click();
    await flushPromises();
    expect(api.getTemplates).toHaveBeenCalled();
    expect(document.body.textContent).toContain(zhTW.designer.template.choose);
    w.unmount();
  });

  it('imports the chosen file text and notifies the template name', async () => {
    const w = await mountPage();
    api.importTemplate.mockResolvedValue({ key: 'k', name: { 'zh-TW': '我的範本' }, source: 'imported', modules: [] });
    const notify = vi.spyOn(quasar.Notify, 'create');
    await pickFile('{"key":"k"}');
    expect(api.importTemplate).toHaveBeenCalledWith('{"key":"k"}');
    expect(notify).toHaveBeenCalledWith(expect.objectContaining({ type: 'positive', message: '已匯入範本：我的範本' }));
    w.unmount();
  });

  it('lists the template validation messages on 400', async () => {
    const w = await mountPage();
    api.importTemplate.mockRejectedValue({ status: 400, errors: { template: ['缺少 key', '模組重複'] } });
    await pickFile('{}');
    const box = q('import-errors')!;
    expect(box.textContent).toContain('缺少 key');
    expect(box.textContent).toContain('模組重複');
    w.unmount();
  });
});
