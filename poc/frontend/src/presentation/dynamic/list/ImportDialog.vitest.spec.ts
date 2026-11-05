import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { ImportPreview, ModuleSchema } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import ImportDialog from './ImportDialog.vue';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { previewImport: vi.fn(), commitImport: vi.fn() } }));
const api = vi.mocked(dynamicApi);

const schema = {
  key: 'm', name: { 'zh-TW': '模組' },
  blocks: [{ id: 1, label: { 'zh-TW': '區' }, collapsed: false, columns: 2, fields: [
    { id: 1, key: 'f_1', type: 'text', label: { 'zh-TW': '標題' }, options: [], config: {} },
    { id: 2, key: 'f_2', type: 'integer', label: { 'zh-TW': '數量' }, options: [], config: {} },
  ] }],
} as unknown as ModuleSchema;

const clean: ImportPreview = {
  columns: [{ header: '標題', fieldKey: 'f_1' }, { header: '備註', ignoredReason: '找不到這個欄位' }], total: 3, valid: 3, errors: [],
};
const dirty: ImportPreview = {
  columns: clean.columns, total: 3, valid: 1,
  errors: [{ line: 3, errors: { f_2: ['必須是整數'] } }, { line: 4, errors: { _record: ['不可重複'] } }],
};
const file = (n = 'a.csv') => new File(['x'], n);

let qc: QueryClient;
async function open() {
  qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(ImportDialog, {
    props: { schema, modelValue: true },
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], i18n, [VueQueryPlugin, { queryClient: qc }]] },
    attachTo: document.body,
  });
  mounted.push(w);
  await flushPromises();
  return w;
}
const pickFile = async (w: Awaited<ReturnType<typeof open>>, f = file()) => {
  w.findComponent(quasar.QFile).vm.$emit('update:modelValue', f);
  await flushPromises();
};
const $ = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
const confirmBtn = () => $('import-confirm') as HTMLButtonElement;

const mounted: { unmount: () => void }[] = [];

describe('ImportDialog', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    // keep body: Quasar's Notify container lives there once created
    for (const w of mounted.splice(0)) w.unmount();
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  it('previews: shows mapped and ignored columns, total/valid counts and row errors by line', async () => {
    api.previewImport.mockResolvedValue(dirty);
    const w = await open();
    await pickFile(w);
    expect(api.previewImport).toHaveBeenCalledWith('m', expect.any(File));
    const cols = $('import-columns')!.textContent!;
    expect(cols).toContain('標題');
    expect(cols).toContain('找不到這個欄位');
    expect($('import-summary')!.textContent).toBe('共 3 筆，1 筆可匯入');
    const rows = [...$('import-errors')!.querySelectorAll('tbody tr')].map((tr) => [...tr.querySelectorAll('td')].map((td) => td.textContent!.trim()));
    expect(rows).toEqual([['3', '數量', '必須是整數'], ['4', '整筆', '不可重複']]);
  });

  it('disables confirm while any row has errors', async () => {
    api.previewImport.mockResolvedValue(dirty);
    const w = await open();
    expect(confirmBtn().disabled).toBe(true);
    await pickFile(w);
    expect(confirmBtn().disabled).toBe(true);
    expect(document.body.textContent).toContain('修正檔案後重新選擇');
    api.previewImport.mockResolvedValue(clean);
    await pickFile(w, file('b.csv'));
    expect(api.previewImport).toHaveBeenCalledTimes(2);
    expect(confirmBtn().disabled).toBe(false);
    expect(document.body.textContent).not.toContain('修正檔案後重新選擇');
  });

  it('disables confirm while the preview is loading', async () => {
    let done!: (p: ImportPreview) => void;
    api.previewImport.mockReturnValue(new Promise((r) => { done = r; }));
    const w = await open();
    await pickFile(w);
    expect(confirmBtn().disabled).toBe(true);
    done(clean);
    await flushPromises();
    expect(confirmBtn().disabled).toBe(false);
  });

  it('confirms, notifies the created count and refetches the list', async () => {
    api.previewImport.mockResolvedValue(clean);
    api.commitImport.mockResolvedValue({ created: 3 });
    const w = await open();
    const invalidate = vi.spyOn(qc, 'invalidateQueries');
    await pickFile(w);
    confirmBtn().click();
    await flushPromises();
    await new Promise((r) => setTimeout(r, 50));
    expect(api.commitImport).toHaveBeenCalledWith('m', expect.any(File));
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['records', 'm'] });
    expect(document.body.textContent).toContain('已匯入 3 筆');
    expect(w.emitted('update:modelValue')?.at(-1)).toEqual([false]);
  });

  it('shows commit rejections in the error table and stays open', async () => {
    api.previewImport.mockResolvedValue(clean);
    api.commitImport.mockRejectedValue({ status: 400, errors: { 'rows[2].f_2': ['參照的資料已不存在'], 'rows[5]._record': ['重複'] } });
    const w = await open();
    await pickFile(w);
    confirmBtn().click();
    await flushPromises();
    const rows = [...$('import-errors')!.querySelectorAll('tbody tr')].map((tr) => [...tr.querySelectorAll('td')].map((td) => td.textContent!.trim()));
    expect(rows).toEqual([['2', '數量', '參照的資料已不存在'], ['5', '整筆', '重複']]);
    expect(w.emitted('update:modelValue')).toBeUndefined();
    expect(confirmBtn().disabled).toBe(true);
  });

  it('shows the server file error (key "file") inline', async () => {
    api.previewImport.mockRejectedValue({ status: 400, errors: { file: ['請把檔案存成 UTF-8 的 CSV'] } });
    const w = await open();
    await pickFile(w);
    expect($('import-file-error')!.textContent).toContain('請把檔案存成 UTF-8 的 CSV');
    expect(confirmBtn().disabled).toBe(true);
  });
});
