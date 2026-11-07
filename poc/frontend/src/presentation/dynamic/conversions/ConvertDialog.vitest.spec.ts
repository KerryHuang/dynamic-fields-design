import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import type { RecordConversion } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import ConvertDialog from './ConvertDialog.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: { convertRecord: vi.fn(), getConversionInputs: vi.fn(), getModuleSchema: vi.fn() },
}));
const api = vi.mocked(dynamicApi);

const line = (lineId: number, quantity: number, converted: number) => ({ lineId, title: `品項${lineId}`, quantity, converted, remaining: quantity - converted });
const conversion = (extra: Partial<RecordConversion> = {}): RecordConversion => ({
  key: 'q2o', name: { 'zh-TW': '報價轉訂單' }, targetModuleKey: 'order', targetName: { 'zh-TW': '訂單' }, allowed: true, targets: [],
  lines: [line(1, 10, 4), line(2, 5, 5)], ...extra,
});

async function mountDialog(c: RecordConversion) {
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/:p(.*)*', component: { render: () => null } }] });
  const push = vi.spyOn(router, 'push');
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(ConvertDialog, {
    props: { modelValue: true, recordId: 5, rowVersion: 'rv1', conversion: c },
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, plugins: { Notify: quasar.Notify } }], createPinia(), router, i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
  });
  await flushPromises();
  return { w, push };
}
const input = (lineId: number) => document.body.querySelector(`[data-testid="convert-quantity-${lineId}"]`) as HTMLInputElement;
const submitBtn = () => document.body.querySelector('[data-testid="convert-submit"]') as HTMLButtonElement;
const type = async (lineId: number, v: string) => { input(lineId).value = v; input(lineId).dispatchEvent(new Event('input')); await flushPromises(); };

describe('ConvertDialog', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    api.getConversionInputs.mockResolvedValue([]);
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });
  afterEach(() => { document.body.innerHTML = ''; });

  it('starts every line at its remaining quantity and shows the figures', async () => {
    const { w } = await mountDialog(conversion());
    expect(input(1).value).toBe('6');
    expect(input(2).value).toBe('0');
    expect(input(2).disabled).toBe(true); // nothing left on that line
    expect(document.body.querySelector('[data-testid="convert-line-1"]')!.textContent).toContain('品項1');
    w.unmount();
  });

  it('refuses more than the remaining quantity, and nothing at all', async () => {
    const { w } = await mountDialog(conversion());
    await type(1, '7');
    expect(document.body.textContent).toContain(zhTW.docflow.convert.error.exceeds);
    expect(submitBtn().disabled).toBe(true);
    await type(1, '0');
    expect(submitBtn().disabled).toBe(true); // at least one line must be converted
    await type(1, '2.5');
    expect(submitBtn().disabled).toBe(false);
    w.unmount();
  });

  it('sends the chosen lines with the source row version and goes to the new document', async () => {
    api.convertRecord.mockResolvedValue({ id: 99, moduleKey: 'order' } as never);
    const { w, push } = await mountDialog(conversion());
    await type(1, '3');
    submitBtn().click();
    await flushPromises();
    expect(api.convertRecord).toHaveBeenCalledWith(5, 'q2o', { rowVersion: 'rv1', lines: [{ sourceLineId: 1, quantity: 3 }] });
    expect(push).toHaveBeenCalledWith('/dynamic/order/99');
    w.unmount();
  });

  it('puts a server quantity error on the line it belongs to', async () => {
    api.convertRecord.mockRejectedValue({ status: 400, errors: { 'lines[0].quantity': ['超過剩餘量'] } });
    const { w, push } = await mountDialog(conversion());
    submitBtn().click();
    await flushPromises();
    expect(document.body.querySelector('[data-testid="convert-line-1"]')!.textContent).toContain('超過剩餘量');
    expect(push).not.toHaveBeenCalled();
    w.unmount();
  });

  it('a header conversion has no table and can be submitted straight away', async () => {
    api.convertRecord.mockResolvedValue({ id: 8, moduleKey: 'opp' } as never);
    const { w } = await mountDialog(conversion({ lines: undefined }));
    expect(document.body.querySelector('table')).toBeNull();
    submitBtn().click();
    await flushPromises();
    expect(api.convertRecord).toHaveBeenCalledWith(5, 'q2o', { rowVersion: 'rv1' });
    w.unmount();
  });
  describe('required target fields the copy cannot fill', () => {
    const schema = {
      key: 'quote', blocks: [{ columns: 1, fields: [
        { id: 7, key: 'f_7', type: 'text', label: { 'zh-TW': '客戶備註' }, config: {}, options: [] },
        { id: 8, key: 'f_8', type: 'text', label: { 'zh-TW': '其他欄位' }, config: {}, options: [] },
      ] }],
    };
    const fieldInput = () => document.body.querySelector('[data-testid="convert-input-f_7"] input') as HTMLInputElement;
    const typeField = async (v: string) => { fieldInput().value = v; fieldInput().dispatchEvent(new Event('input')); await flushPromises(); };

    it('shows only the fields the server asked for, and asks nothing when there are none', async () => {
      api.getConversionInputs.mockResolvedValue(['f_7']);
      api.getModuleSchema.mockResolvedValue(schema as never);
      const { w } = await mountDialog(conversion({ lines: undefined }));
      expect(fieldInput()).not.toBeNull();
      expect(document.body.querySelector('[data-testid="convert-input-f_8"]')).toBeNull();
      expect(api.getConversionInputs).toHaveBeenCalledWith(5, 'q2o');
      w.unmount();
      document.body.innerHTML = '';
      api.getConversionInputs.mockResolvedValue([]);
      const empty = await mountDialog(conversion({ lines: undefined }));
      expect(document.body.querySelector('[data-testid="convert-inputs"]')).toBeNull();
      expect(submitBtn().disabled).toBe(false);
      empty.w.unmount();
    });

    it('keeps convert disabled until filled, then sends the value with the request', async () => {
      api.getConversionInputs.mockResolvedValue(['f_7']);
      api.getModuleSchema.mockResolvedValue(schema as never);
      api.convertRecord.mockResolvedValue({ id: 99, moduleKey: 'quote' } as never);
      const { w } = await mountDialog(conversion({ lines: undefined }));
      expect(submitBtn().disabled).toBe(true);
      await typeField('急件');
      expect(submitBtn().disabled).toBe(false);
      submitBtn().click();
      await flushPromises();
      expect(api.convertRecord).toHaveBeenCalledWith(5, 'q2o', { rowVersion: 'rv1', values: { f_7: '急件' } });
      w.unmount();
    });

    it('adds a field the rules made required only after the first attempt, shows its error and sends it on the retry', async () => {
      api.getConversionInputs.mockResolvedValue([]); // 一開始沒有要補的欄位
      api.getModuleSchema.mockResolvedValue(schema as never);
      api.convertRecord.mockRejectedValueOnce({ status: 400, errors: { f_8: ['此欄位必填'], 'lines[0].quantity': ['x'] } });
      api.convertRecord.mockResolvedValue({ id: 99, moduleKey: 'quote' } as never);
      const { w } = await mountDialog(conversion({ lines: undefined }));
      expect(document.body.querySelector('[data-testid="convert-input-f_8"]')).toBeNull();

      submitBtn().click();
      await flushPromises();

      const field = document.body.querySelector('[data-testid="convert-input-f_8"]');
      expect(field).not.toBeNull();
      expect(field!.textContent).toContain('此欄位必填');
      expect(submitBtn().disabled).toBe(true);
      const box = field!.querySelector('input') as HTMLInputElement;
      box.value = '補上';
      box.dispatchEvent(new Event('input'));
      await flushPromises();
      expect(submitBtn().disabled).toBe(false);
      submitBtn().click();
      await flushPromises();
      expect(api.convertRecord).toHaveBeenLastCalledWith(5, 'q2o', { rowVersion: 'rv1', values: { f_8: '補上' } });
      w.unmount();
    });

    it('says so, stops loading and keeps convert disabled when the target schema cannot be read', async () => {
      api.getConversionInputs.mockResolvedValue(['f_7']);
      api.getModuleSchema.mockRejectedValue({ status: 403 }); // 有新增權限、沒有讀取權限
      const { w } = await mountDialog(conversion({ lines: undefined }));
      expect(document.body.querySelector('[data-testid="convert-inputs-loading"]')).toBeNull();
      expect(document.body.querySelector('[data-testid="convert-inputs-failed"]')!.textContent).toContain(zhTW.docflow.convert.inputsFailed);
      expect(submitBtn().disabled).toBe(true);
      w.unmount();
    });

    it('puts a server error for the field on the field itself', async () => {
      api.getConversionInputs.mockResolvedValue(['f_7']);
      api.getModuleSchema.mockResolvedValue(schema as never);
      api.convertRecord.mockRejectedValue({ status: 400, errors: { f_7: ['不合法的值'] } });
      const { w } = await mountDialog(conversion({ lines: undefined }));
      await typeField('x');
      submitBtn().click();
      await flushPromises();
      expect(document.body.textContent).toContain('不合法的值');
      w.unmount();
    });
  });
});
