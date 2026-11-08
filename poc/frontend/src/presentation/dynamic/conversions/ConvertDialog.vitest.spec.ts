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

// 通知容器只在第一次通知時建立，跨測試不穩：改記錄 $q.notify 的呼叫來驗證
const notified = vi.hoisted(() => vi.fn());
vi.mock('quasar', async (orig) => {
  const actual = await orig<typeof import('quasar')>();
  return {
    ...actual,
    useQuasar: () => {
      const q = actual.useQuasar();
      return Object.create(q, { notify: { value: (o: unknown) => { notified(o); return (q.notify as (o: unknown) => unknown)(o); } } });
    },
  };
});
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
    expect(api.convertRecord).toHaveBeenCalledWith(5, 'q2o', { rowVersion: 'rv1', lines: [{ sourceLineId: 1, quantity: 3 }] }, expect.any(String));
    expect(push).toHaveBeenCalledWith('/dynamic/order/99');
    w.unmount();
  });

  it('a retry after a failed send reuses the Idempotency-Key, and a reopened dialog gets a new one', async () => {
    api.convertRecord.mockRejectedValueOnce({ status: 0 }).mockResolvedValue({ id: 99, moduleKey: 'order' } as never);
    const { w } = await mountDialog(conversion());
    await type(1, '3');
    submitBtn().click();
    await flushPromises();
    submitBtn().click();
    await flushPromises();
    const keys = api.convertRecord.mock.calls.map((c) => c[3]);
    expect(keys[0]).toMatch(/^[A-Za-z0-9_-]{8,64}$/);
    expect(keys[1]).toBe(keys[0]);
    w.unmount();
    document.body.innerHTML = '';
    const again = await mountDialog(conversion());
    await type(1, '3');
    submitBtn().click();
    await flushPromises();
    expect(api.convertRecord.mock.calls[2]![3]).not.toBe(keys[0]);
    again.w.unmount();
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
    expect(api.convertRecord).toHaveBeenCalledWith(5, 'q2o', { rowVersion: 'rv1' }, expect.any(String));
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
      expect(api.convertRecord).toHaveBeenCalledWith(5, 'q2o', { rowVersion: 'rv1', values: { f_7: '急件' } }, expect.any(String));
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
      const box = field!.querySelector('input') as HTMLInputElement;
      box.value = '補上';
      box.dispatchEvent(new Event('input'));
      await flushPromises();
      expect(submitBtn().disabled).toBe(false);
      submitBtn().click();
      await flushPromises();
      expect(api.convertRecord).toHaveBeenLastCalledWith(5, 'q2o', { rowVersion: 'rv1', values: { f_8: '補上' } }, expect.any(String));
      w.unmount();
    });

    it('a field added by a 400 is shown and sent when filled, but an empty one does not block convert (it may be optional)', async () => {
      api.getConversionInputs.mockResolvedValue([]);
      api.getModuleSchema.mockResolvedValue(schema as never);
      api.convertRecord.mockRejectedValueOnce({ status: 400, errors: { f_8: ['請確認這個欄位'] } });
      api.convertRecord.mockResolvedValue({ id: 99, moduleKey: 'quote' } as never);
      const { w } = await mountDialog(conversion({ lines: undefined }));
      submitBtn().click();
      await flushPromises();

      const field = document.body.querySelector('[data-testid="convert-input-f_8"]')!;
      expect(field.textContent).toContain('請確認這個欄位');
      expect(submitBtn().disabled).toBe(false); // 選填欄位留空不擋按鈕
      const box = field.querySelector('input') as HTMLInputElement;
      box.value = '改好了';
      box.dispatchEvent(new Event('input'));
      await flushPromises();
      submitBtn().click();
      await flushPromises();
      expect(api.convertRecord).toHaveBeenLastCalledWith(5, 'q2o', { rowVersion: 'rv1', values: { f_8: '改好了' } }, expect.any(String));
      w.unmount();
    });

    it('a quantity-only 400 does not fetch the schema, so a user who cannot read the target is never locked out', async () => {
      api.getConversionInputs.mockResolvedValue([]);
      api.getModuleSchema.mockRejectedValue({ status: 403 });
      api.convertRecord.mockRejectedValue({ status: 400, errors: { 'lines[0].quantity': ['超過剩餘量'] } });
      const { w } = await mountDialog(conversion());
      submitBtn().click();
      await flushPromises();

      expect(api.getModuleSchema).not.toHaveBeenCalled();
      expect(document.body.querySelector('[data-testid="convert-inputs-failed"]')).toBeNull();
      await type(1, '3');
      expect(submitBtn().disabled).toBe(false);
      w.unmount();
    });

    it('a field error whose schema cannot be read goes to the notification and leaves convert enabled', async () => {
      api.getConversionInputs.mockResolvedValue([]);
      api.getModuleSchema.mockRejectedValue({ status: 403 });
      api.convertRecord.mockRejectedValue({ status: 400, errors: { f_9: ['這個欄位有問題'] } });
      const { w } = await mountDialog(conversion({ lines: undefined }));
      submitBtn().click();
      await flushPromises();

      expect(notified).toHaveBeenCalledWith(expect.objectContaining({ type: 'negative', message: '這個欄位有問題' }));
      expect(document.body.querySelector('[data-testid="convert-inputs-failed"]')).toBeNull();
      expect(submitBtn().disabled).toBe(false);
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
