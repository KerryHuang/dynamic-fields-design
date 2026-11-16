import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi, type TemplateSummary } from 'src/services/api/dynamic-api';
import TemplateDialog from './TemplateDialog.vue';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { getTemplates: vi.fn(), instantiateTemplate: vi.fn() } }));
const api = vi.mocked(dynamicApi);

const f = (code: string, extra: object = {}) => ({ code, label: { 'zh-TW': code }, type: 'text', locked: false, dependsOn: [], ...extra });
const templates: TemplateSummary[] = [{
  key: 'sales', name: { 'zh-TW': '銷售' }, description: { 'zh-TW': '訂單與明細' }, source: 'builtIn',
  modules: [
    { code: 'order', key: 'order', name: { 'zh-TW': '訂單' }, fields: [
      f('no', { locked: true }), f('subtotal'), f('taxRate'), f('tax', { dependsOn: ['order.subtotal', 'order.taxRate'] }),
    ] },
    { code: 'line', key: 'order-line', name: { 'zh-TW': '明細' }, detailOf: 'order', fields: [f('order', { locked: true }), f('qty')] },
  ],
}, { key: 'other', name: { 'zh-TW': '其他' }, source: 'imported', modules: [] }];

let router: ReturnType<typeof createRouter>;
async function mountDialog() {
  api.getTemplates.mockResolvedValue(templates);
  router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', component: { render: () => null } }, { path: '/system/dynamic-modules/:k', component: { render: () => null } }] });
  await router.push('/');
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(TemplateDialog, {
    props: { modelValue: true }, attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, plugins: { Notify: quasar.Notify } }], router, i18n,
      [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]] },
  });
  await flushPromises();
  return w;
}
const q = (id: string) => document.body.querySelector(`[data-testid="${CSS.escape(id)}"]`) as HTMLElement | null;
const click = async (id: string) => { q(id)!.click(); await flushPromises(); };

describe('TemplateDialog', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    document.body.innerHTML = '';
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  it('step 1 lists templates with name, description and source', async () => {
    const w = await mountDialog();
    expect(q('template-sales')!.textContent).toContain('銷售');
    expect(q('template-sales')!.textContent).toContain('訂單與明細');
    expect(q('template-sales')!.textContent).toContain('內建');
    expect(q('template-other')!.textContent).toContain('已匯入');
    w.unmount();
  });

  it('step 2 shows a card per module; locked fields are disabled; submit sends unchecked qualified names', async () => {
    const w = await mountDialog();
    await click('template-sales');
    expect(q('module-card-order')).not.toBeNull();
    expect(q('module-card-line')!.textContent).toContain('明細（訂單）');
    expect((q('module-key-order') as HTMLInputElement).value).toBe('order');
    expect(q('field-order.no')!.getAttribute('aria-disabled')).toBe('true');
    await click('field-order.taxRate');
    expect(q('field-order.tax')!.getAttribute('aria-checked')).toBe('false');
    expect(q('field-order.subtotal')!.getAttribute('aria-checked')).toBe('true');
    await click('field-line.qty');
    api.instantiateTemplate.mockResolvedValue({ modules: [{ code: 'order', key: 'order', id: 1 }, { code: 'line', key: 'order-line', id: 2 }], warnings: ['群組「x」不存在，已略過它的權限建議'] });
    const notify = vi.spyOn(quasar.Notify, 'create');
    await click('template-create');
    expect(api.instantiateTemplate).toHaveBeenCalledWith('sales', {
      modules: [
        { code: 'order', key: 'order', name: { 'zh-TW': '訂單' } },
        { code: 'line', key: 'order-line', name: { 'zh-TW': '明細' } },
      ],
      excludeFields: ['order.taxRate', 'order.tax', 'line.qty'],
    });
    expect(notify).toHaveBeenCalledWith(expect.objectContaining({ type: 'warning', message: '群組「x」不存在，已略過它的權限建議' }));
    expect(router.currentRoute.value.path).toBe('/system/dynamic-modules/order');
    w.unmount();
  });

  it('shows prefixed 400 errors and 409 messages at the top', async () => {
    const w = await mountDialog();
    await click('template-sales');
    api.instantiateTemplate.mockRejectedValueOnce({ status: 400, errors: { 'order.no.Config.Format': ['格式不正確'] } });
    await click('template-create');
    expect(q('template-error')!.textContent).toContain('格式不正確');
    api.instantiateTemplate.mockRejectedValueOnce({ status: 409, message: '模組「line」：模組代碼已存在' });
    await click('template-create');
    expect(q('template-error')!.textContent).toContain('模組「line」：模組代碼已存在');
    expect(router.currentRoute.value.path).toBe('/');
    w.unmount();
  });
});
