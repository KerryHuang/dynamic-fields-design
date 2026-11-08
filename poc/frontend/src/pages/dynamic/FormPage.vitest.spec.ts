import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import type { ModuleSchema, SchemaDetail, SchemaField, SchemaPermissions } from 'src/domain/dynamic/types';
import DetailGrid from 'src/presentation/dynamic/details/DetailGrid.vue';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import PopupSelect from 'src/components/framework/PopupSelect.vue';
import FormPage from './FormPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: { getModuleSchema: vi.fn(), getRecord: vi.fn(), createRecord: vi.fn(), updateRecord: vi.fn(), light: vi.fn() },
}));
const api = vi.mocked(dynamicApi);

const field = (id: number, label: string, readOnly = false): SchemaField => ({
  id, key: `f_${id}`, type: 'text', label: { 'zh-TW': label }, options: [],
  config: { required: false, readOnly, showInList: true, isFilterable: true, isSortable: true },
});
const all: SchemaPermissions = { read: true, add: true, edit: true, delete: true, printExport: true, pricing: true };
const schema = (permissions: SchemaPermissions, fields: SchemaField[] = [field(1, '標題'), field(2, '甲')]): ModuleSchema => ({
  key: 'm', programCode: 'DYN_M', name: { 'zh-TW': '模組' }, titleFieldKey: 'f_1', version: 1, rules: [], permissions,
  blocks: [{ id: 1, label: { 'zh-TW': '區' }, collapsed: false, columns: 2, fields }],
});
const record = { id: 5, moduleKey: 'm', title: 'r', values: { f_1: 'r', f_2: 'x' }, ownerUserId: 'a', createdUserId: 'a', createdTime: '', modifiedUserId: 'a', modifiedTime: '', rowVersion: 'v', references: {}, hostKey: null };

async function mountPage(s: ModuleSchema, path: string, rec: typeof record = record) {
  setActivePinia(createPinia());
  api.getModuleSchema.mockResolvedValue(s);
  api.getRecord.mockResolvedValue(rec as never);
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/dynamic/:moduleKey/new', component: FormPage }, { path: '/dynamic/:moduleKey/:id/edit', component: FormPage }],
  });
  await router.push(path);
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const Host = { render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(FormPage))) };
  const w = mount(Host, {
    global: {
      plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], router, i18n,
        [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]],
    },
  });
  await flushPromises();
  return w;
}

describe('FormPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  it('FormPage shows forbidden in edit mode without edit', async () => {
    const w = await mountPage(schema({ ...all, edit: false }), '/dynamic/m/5/edit');
    expect(w.find('[data-testid="forbidden"]').exists()).toBe(true);
    expect(w.find('form').exists()).toBe(false);
  });

  it('FormPage shows forbidden in create mode without add, but not when only edit is missing', async () => {
    const noAdd = await mountPage(schema({ ...all, add: false }), '/dynamic/m/new');
    expect(noAdd.find('[data-testid="forbidden"]').exists()).toBe(true);
    expect(noAdd.find('form').exists()).toBe(false);
    const noEdit = await mountPage(schema({ ...all, edit: false }), '/dynamic/m/new');
    expect(noEdit.find('[data-testid="forbidden"]').exists()).toBe(false);
    expect(noEdit.find('form').exists()).toBe(true);
  });

  it('FormPage renders a read-only field as readonly', async () => {
    const w = await mountPage(schema(all, [field(1, '標題'), field(2, '甲', true)]), '/dynamic/m/5/edit');
    const inputs = w.findAll('input');
    expect(inputs).toHaveLength(2);
    expect(inputs[0]!.attributes('readonly')).toBeUndefined();
    expect(inputs[1]!.attributes('readonly')).toBeDefined();
  });

  describe('references', () => {
    const refField = (id: number, kind: 'module' | 'entity'): SchemaField => ({
      ...field(id, `參照${id}`), type: 'reference',
      reference: { kind, key: kind === 'module' ? 'cust' : 'design.mold', name: { 'zh-TW': 'x' }, lightUrl: kind === 'module' ? '/modules/cust/records/light' : '/entities/design.mold/light' },
    });
    const refSchema = (fields: SchemaField[], extra: Partial<ModuleSchema> = {}) => ({ ...schema(all, [field(1, '標題'), ...fields]), ...extra });

    it('edit form passes reference title to PopupSelect', async () => {
      const rec = { ...record, values: { f_1: 'r', f_12: 42 }, references: { f_12: { key: '42', title: '客戶甲', state: 'ok' } } };
      const w = await mountPage(refSchema([refField(12, 'module')]), '/dynamic/m/5/edit', rec as never);
      expect(w.findComponent(PopupSelect).props('reference')).toMatchObject({ key: '42', title: '客戶甲' });
      expect((w.find('input[data-testid="popup-input"]').element as HTMLInputElement).value).toBe('客戶甲');
    });

    it('prefill query sets reference initial value', async () => {
      const w = await mountPage(refSchema([refField(12, 'module')]), '/dynamic/m/new?prefill=f_12:42');
      expect(w.findComponent(PopupSelect).props('modelValue')).toBe(42);
      await w.find('input').setValue('t');
      await w.find('form').trigger('submit');
      await vi.waitFor(() => expect(api.createRecord).toHaveBeenCalledWith('m', expect.objectContaining({ f_12: 42 }), undefined, undefined, expect.any(String)));
    });

    it('prefill shows the target record title', async () => {
      const w = await mountPage(refSchema([refField(12, 'module')]), '/dynamic/m/new?prefill=f_12:42', { ...record, id: 42, title: '客戶甲' });
      expect(api.getRecord).toHaveBeenCalledWith(42);
      expect((w.find('input[data-testid="popup-input"]').element as HTMLInputElement).value).toBe('客戶甲');
    });

    it('prefill ignores non-reference or unknown fields', async () => {
      const w1 = await mountPage(refSchema([refField(12, 'module')]), '/dynamic/m/new?prefill=f_1:5');
      expect((w1.find('input').element as HTMLInputElement).value).toBe('');
      const w2 = await mountPage(refSchema([refField(12, 'module')]), '/dynamic/m/new?prefill=f_99:5');
      expect(w2.findComponent(PopupSelect).props('modelValue') ?? null).toBeNull();
      const w3 = await mountPage(refSchema([refField(12, 'module')]), '/dynamic/m/new?prefill=f_12:abc');
      expect(w3.findComponent(PopupSelect).props('modelValue') ?? null).toBeNull();
      const w4 = await mountPage(refSchema([refField(13, 'entity')]), '/dynamic/m/new?prefill=f_13:M-0001');
      expect(w4.findComponent(PopupSelect).props('modelValue')).toBe('M-0001');
    });

    it('extension module new form requires host pick', async () => {
      api.light.mockResolvedValue({ items: [{ key: 'M-0001', title: 'M-0001 外殼上蓋模' }], total: 1 });
      api.createRecord.mockResolvedValue({ ...record, id: 9 } as never);
      const w = await mountPage(refSchema([], { kind: 'extension', hostEntityKey: 'design.mold', titleFieldKey: null }), '/dynamic/m/new');
      const host = w.findComponent(PopupSelect);
      expect(host.props('lightUrl')).toBe('/entities/design.mold/light');
      expect(host.props('valueKind')).toBe('string');
      await w.find('form').trigger('submit');
      await new Promise((r) => setTimeout(r, 50));
      await flushPromises();
      expect(api.createRecord).not.toHaveBeenCalled();
      expect(w.text()).toContain(zhTW.designer.extension.hostEntityRequired);
      host.vm.$emit('update:modelValue', 'M-0001');
      await flushPromises();
      await w.find('form').trigger('submit');
      await vi.waitFor(() => expect(api.createRecord).toHaveBeenCalledWith('m', expect.anything(), 'M-0001', undefined, expect.any(String)));
    });

    it('create-mode 409 shows the server message, not the stale notice', async () => {
      api.light.mockResolvedValue({ items: [], total: 0 });
      api.createRecord.mockRejectedValue({ status: 409, message: '這筆既有資料已經有擴充資料' });
      const w = await mountPage(refSchema([], { kind: 'extension', hostEntityKey: 'design.mold', titleFieldKey: null }), '/dynamic/m/new');
      w.findComponent(PopupSelect).vm.$emit('update:modelValue', 'M-0001');
      await flushPromises();
      await w.find('form').trigger('submit');
      await vi.waitFor(() => expect(w.text()).toContain('這筆既有資料已經有擴充資料'));
      expect(document.body.textContent).not.toContain(zhTW.dynamic.stale);
    });

    it('create-mode hostKey 400 lands on the host picker without a schema refetch', async () => {
      api.createRecord.mockRejectedValue({ status: 400, message: 'x', errors: { hostKey: ['既有資料不存在'] } });
      const w = await mountPage(refSchema([], { kind: 'extension', hostEntityKey: 'design.mold', titleFieldKey: null }), '/dynamic/m/new');
      w.findComponent(PopupSelect).vm.$emit('update:modelValue', 'M-0001');
      await flushPromises();
      await w.find('form').trigger('submit');
      await vi.waitFor(() => expect(w.findComponent(PopupSelect).props('error')).toBe('既有資料不存在'));
      expect(api.getModuleSchema).toHaveBeenCalledTimes(1);
      expect(document.body.textContent).not.toContain('既有資料不存在');
    });

    it('edit-mode stale 409 still shows the stale notice', async () => {
      api.updateRecord.mockRejectedValue({ status: 409, message: '資料已被其他人修改，請重新載入' });
      const w = await mountPage(refSchema([]), '/dynamic/m/5/edit');
      await w.find('input').setValue('y');
      await w.find('form').trigger('submit');
      await vi.waitFor(() => expect(document.body.textContent).toContain(zhTW.dynamic.stale));
    });
  });

  describe('details', () => {
    const line: SchemaDetail = {
      moduleKey: 'line', name: { 'zh-TW': '訂單明細' }, parentFieldId: 9, parentFieldKey: 'f_9', titleFieldKey: 'f_11',
      fields: [field(11, '品名'), field(12, '規格')], rules: [],
    };
    const detailSchema = (): ModuleSchema => ({ ...schema(all), details: [line] });
    const row = (id: number, values: Record<string, unknown>) => ({ ...record, id, moduleKey: 'line', values, rowVersion: `v${id}` });
    const header = { ...record, details: { line: [row(31, { f_11: '螺絲', f_12: 'M3' }), row(32, { f_11: '螺帽', f_12: 'M4' })] } };
    const gridRows = (w: Awaited<ReturnType<typeof mountPage>>) => w.findComponent(DetailGrid).findAll('tbody tr');
    const submit = async (w: Awaited<ReturnType<typeof mountPage>>) => {
      await w.find('form').trigger('submit');
      await new Promise((r) => setTimeout(r, 30));
      await flushPromises();
    };

    it('create sends details', async () => {
      api.createRecord.mockResolvedValue({ ...record, id: 9 } as never);
      const w = await mountPage(detailSchema(), '/dynamic/m/new');
      await w.find('input').setValue('單頭');
      await w.find('[data-testid="add-row"]').trigger('click');
      await gridRows(w)[0]!.find('input').setValue('螺絲');
      await submit(w);
      expect(api.createRecord).toHaveBeenCalledWith('m', expect.objectContaining({ f_1: '單頭' }), undefined, {
        line: [{ values: { f_11: '螺絲', f_12: null } }],
      }, expect.any(String));
    });

    it('edit sends only changed rows', async () => {
      api.updateRecord.mockResolvedValue({ ...record } as never);
      const w = await mountPage(detailSchema(), '/dynamic/m/5/edit', header as never);
      await w.find('input').setValue('新標題');
      await gridRows(w)[1]!.findAll('input')[1]!.setValue('M5');
      await submit(w);
      expect(api.updateRecord).toHaveBeenCalledWith(5, { f_1: '新標題' }, 'v', { line: [{ id: 32, rowVersion: 'v32', values: { f_12: 'M5' } }] }, expect.any(String));
    });

    it('edit with no header change but detail change still saves', async () => {
      api.updateRecord.mockResolvedValue({ ...record } as never);
      const w = await mountPage(detailSchema(), '/dynamic/m/5/edit', header as never);
      await w.findAll('[data-testid="delete-row"]')[0]!.trigger('click');
      await submit(w);
      expect(api.updateRecord).toHaveBeenCalledWith(5, {}, 'v', { line: [{ id: 31, rowVersion: 'v31', _delete: true }] }, expect.any(String));
    });

    it('400 detail error keys shown on rows, not as outside notify', async () => {
      api.updateRecord.mockRejectedValue({
        status: 400, message: 'x',
        errors: { 'details.line[0].f_12': ['規格不正確'], 'details.line[1]._record': ['此記錄目前不可修改'], 'details.line': ['明細最多 200 列'] },
      });
      const w = await mountPage(detailSchema(), '/dynamic/m/5/edit', header as never);
      await gridRows(w)[0]!.findAll('input')[1]!.setValue('X');
      await gridRows(w)[1]!.findAll('input')[1]!.setValue('Y');
      await submit(w);
      await vi.waitFor(() => expect(gridRows(w)[0]!.text()).toContain('規格不正確'));
      expect(gridRows(w)[1]!.find('[data-testid="row-error"]').text()).toContain('此記錄目前不可修改');
      const notified = document.body.querySelector('.q-notifications')?.textContent ?? '';
      expect(notified).toContain('明細最多 200 列');
      expect(notified).not.toContain('規格不正確');
      expect(notified).not.toContain('此記錄目前不可修改');
    });

    it('409 stale on detail shows reload', async () => {
      api.updateRecord.mockRejectedValue({ status: 409, message: '資料已被其他人修改，請重新載入' });
      const w = await mountPage(detailSchema(), '/dynamic/m/5/edit', header as never);
      await gridRows(w)[0]!.findAll('input')[1]!.setValue('X');
      await submit(w);
      await vi.waitFor(() => expect(document.body.textContent).toContain(zhTW.dynamic.stale));
      expect(api.updateRecord).toHaveBeenCalledWith(5, {}, 'v', { line: [{ id: 31, rowVersion: 'v31', values: { f_12: 'X' } }] }, expect.any(String));
      expect(document.body.textContent).toContain(zhTW.dynamic.reload);
    });
  });
  describe('Idempotency-Key', () => {
    const submit = async (w: Awaited<ReturnType<typeof mountPage>>) => {
      await w.find('form').trigger('submit');
      await new Promise((r) => setTimeout(r, 30));
      await flushPromises();
    };
    const keyOf = (fn: { mock: { calls: unknown[][] } }, call: number, arg: number) => fn.mock.calls[call]![arg];

    it('a failed create is resent with the same key, a new form gets another', async () => {
      api.createRecord.mockRejectedValueOnce({ status: 0 }).mockResolvedValue({ ...record, id: 9 } as never);
      const w = await mountPage(schema(all), '/dynamic/m/new');
      await w.find('input').setValue('甲');
      await submit(w);
      await submit(w);
      const first = keyOf(api.createRecord, 0, 4);
      expect(first).toMatch(/^[A-Za-z0-9_-]{8,64}$/);
      expect(keyOf(api.createRecord, 1, 4)).toBe(first);
      const other = await mountPage(schema(all), '/dynamic/m/new');
      api.createRecord.mockResolvedValue({ ...record, id: 10 } as never);
      await other.find('input').setValue('乙');
      await submit(other);
      expect(keyOf(api.createRecord, 2, 4)).not.toBe(first);
    });

    it('an edit carries the key too, and a 422 (same key, other content) takes a fresh key for the next try', async () => {
      api.updateRecord.mockRejectedValueOnce({ status: 422, message: '同一個操作識別碼用在不同內容' }).mockResolvedValue({ ...record } as never);
      const w = await mountPage(schema(all), '/dynamic/m/5/edit');
      await w.find('input').setValue('y');
      await submit(w);
      await submit(w);
      expect(keyOf(api.updateRecord, 0, 4)).toMatch(/^[A-Za-z0-9_-]{8,64}$/);
      expect(keyOf(api.updateRecord, 1, 4)).not.toBe(keyOf(api.updateRecord, 0, 4));
    });
  });
});
