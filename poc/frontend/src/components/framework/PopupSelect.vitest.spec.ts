import { flushPromises, mount } from '@vue/test-utils';
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import PopupSelect from './PopupSelect.vue';

const light = vi.spyOn(dynamicApi, 'light');
const URL = '/modules/customer/records/light';

function mountSelect(props: Record<string, unknown> = {}) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  return mount(PopupSelect, {
    props: { modelValue: null, lightUrl: URL, valueKind: 'number', label: '客戶', ...props },
    global: {
      plugins: [[quasar.Quasar, { components: quasar }], i18n,
        [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]],
    },
    attachTo: document.body,
  });
}
const items = () => [...document.body.querySelectorAll('[data-testid="popup-item"]')] as HTMLElement[];
const dialogText = () => document.body.querySelector('[data-testid="popup-dialog"]')?.textContent ?? '';
async function openDialog(w: ReturnType<typeof mountSelect>) {
  await w.get('[data-testid="popup-input"]').trigger('click');
  await flushPromises();
}

describe('PopupSelect', () => {
  beforeEach(() => {
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
    light.mockReset();
    light.mockResolvedValue({ items: [{ key: '1', title: '台灣精密' }, { key: '2', title: '宏達機械' }], total: 2 });
  });
  afterEach(() => { vi.useRealTimers(); document.body.innerHTML = ''; });

  it('opens dialog and lists light items', async () => {
    const w = mountSelect();
    expect(light).not.toHaveBeenCalled();
    await openDialog(w);
    expect(light).toHaveBeenCalledWith(URL, { keyword: '', page: 1, size: 10 });
    expect(items().map((i) => i.textContent?.trim())).toEqual(['台灣精密', '宏達機械']);
    w.unmount();
  });

  it('keyword triggers search after debounce', async () => {
    const w = mountSelect();
    await openDialog(w);
    vi.useFakeTimers();
    const input = document.body.querySelector('[data-testid="popup-keyword"]') as HTMLInputElement;
    input.value = '宏';
    input.dispatchEvent(new Event('input'));
    await vi.advanceTimersByTimeAsync(299);
    expect(light).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(1);
    vi.useRealTimers();
    await flushPromises();
    expect(light).toHaveBeenLastCalledWith(URL, { keyword: '宏', page: 1, size: 10 });
    w.unmount();
  });

  it('select emits number when valueKind is number', async () => {
    const w = mountSelect();
    await openDialog(w);
    items()[1]!.click();
    await flushPromises();
    expect(w.emitted('update:modelValue')).toEqual([[2]]);
    w.unmount();
  });

  it('select emits string when valueKind is string', async () => {
    light.mockResolvedValue({ items: [{ key: 'M-0001', title: 'M-0001 外殼上蓋模' }], total: 1 });
    const w = mountSelect({ valueKind: 'string' });
    await openDialog(w);
    items()[0]!.click();
    await flushPromises();
    expect(w.emitted('update:modelValue')).toEqual([['M-0001']]);
    w.unmount();
  });

  it('shows displayTitle for the current value', () => {
    const w = mountSelect({ modelValue: 7, reference: { key: '7', title: '永豐電子', state: 'ok' } });
    expect((w.get('input').element as HTMLInputElement).value).toBe('永豐電子');
    w.unmount();
  });

  it('a freshly picked title wins over the stored reference title', async () => {
    light.mockResolvedValue({ items: [{ key: '8', title: '宏達機械' }], total: 1 });
    const w = mountSelect({ modelValue: 7, reference: { key: '7', title: '永豐電子', state: 'ok' } });
    await openDialog(w);
    items()[0]!.click();
    await flushPromises();
    await w.setProps({ modelValue: 8 });
    expect((w.get('input').element as HTMLInputElement).value).toBe('宏達機械');
    w.unmount();
  });

  it.each([['missing', zhTW.dynamic.reference.missing], ['restricted', zhTW.dynamic.reference.restricted]])(
    'reference state %s shows the state text instead of the key', (state, text) => {
      const w = mountSelect({ modelValue: 7, reference: { key: '7', state } });
      expect((w.get('input').element as HTMLInputElement).value).toBe(text);
      w.unmount();
    });

  it('light query error shows restricted text instead of empty', async () => {
    light.mockRejectedValue({ status: 403 });
    const w = mountSelect();
    await openDialog(w);
    expect(dialogText()).toContain(zhTW.dynamic.reference.restricted);
    expect(dialogText()).not.toContain(zhTW.dynamic.reference.empty);
    w.unmount();
  });

  it('clear emits null', async () => {
    const w = mountSelect({ modelValue: 1 });
    await openDialog(w);
    (document.body.querySelector('[data-testid="popup-clear"]') as HTMLElement).click();
    await flushPromises();
    expect(w.emitted('update:modelValue')).toEqual([[null]]);
    w.unmount();
  });

  it('readonly does not open dialog', async () => {
    const w = mountSelect({ readonly: true });
    await openDialog(w);
    expect(light).not.toHaveBeenCalled();
    expect(dialogText()).toBe('');
    w.unmount();
  });

  it('multiple emits array', async () => {
    const w = mountSelect({ multiple: true, modelValue: [] });
    await openDialog(w);
    items()[0]!.click();
    await flushPromises();
    items()[1]!.click();
    await flushPromises();
    (document.body.querySelector('[data-testid="popup-confirm"]') as HTMLElement).click();
    await flushPromises();
    expect(w.emitted('update:modelValue')).toEqual([[[1, 2]]]);
    w.unmount();
  });
});
