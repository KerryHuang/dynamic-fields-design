import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import type { DynamicRecord, ModuleSchema, SchemaDetail, SchemaField, SchemaPermissions } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import DetailPage from './DetailPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: { getModuleSchema: vi.fn(), getRecord: vi.fn(), deleteRecord: vi.fn(), related: vi.fn(), recordHistory: vi.fn() },
}));
const api = vi.mocked(dynamicApi);

const field = (id: number, label: string): SchemaField => ({
  id, key: `f_${id}`, type: 'text', label: { 'zh-TW': label }, options: [],
  config: { required: false, readOnly: false, showInList: true, isFilterable: true, isSortable: true },
});
const all: SchemaPermissions = { read: true, add: true, edit: true, delete: true, printExport: true, pricing: true };
const schema = (p: Partial<ModuleSchema>): ModuleSchema => ({
  key: 'm', programCode: 'DYN_M', name: { 'zh-TW': '模組' }, titleFieldKey: 'f_1', version: 1, rules: [], permissions: all,
  blocks: [{ id: 1, label: { 'zh-TW': '區' }, collapsed: false, fields: [field(1, '標題'), field(2, '甲')] }], ...p,
});
const record: DynamicRecord = { id: 5, moduleKey: 'm', title: '記錄五', values: { f_1: '記錄五', f_2: 'x' } as Record<string, unknown>, ownerUserId: 'a', createdUserId: 'a', createdTime: '', modifiedUserId: 'a', modifiedTime: '', rowVersion: 'v', references: {}, hostKey: null };

async function mountPage(s: ModuleSchema, rec: DynamicRecord = record, tabs: unknown[] = []) {
  setActivePinia(createPinia());
  api.getModuleSchema.mockResolvedValue(s);
  api.getRecord.mockResolvedValue(rec);
  api.related.mockResolvedValue(tabs as never);
  api.recordHistory.mockResolvedValue({ items: [], totalCount: 0, currentPage: 1, pagesSize: 20 });
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/dynamic/:moduleKey/:id', component: DetailPage }] });
  await router.push('/dynamic/m/5');
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const Host = { render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(DetailPage))) };
  const w = mount(Host, {
    global: {
      plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], router, i18n,
        [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]],
    },
  });
  await flushPromises();
  return w;
}

describe('DetailPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    document.querySelectorAll('.q-dialog').forEach((el) => el.remove());
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  it('DetailPage hides edit and delete per permissions', async () => {
    const full = await mountPage(schema({}));
    expect(full.text()).toContain(zhTW.dynamic.edit);
    expect(full.text()).toContain(zhTW.dynamic.delete);
    const noEdit = await mountPage(schema({ permissions: { ...all, edit: false } }));
    expect(noEdit.text()).not.toContain(zhTW.dynamic.edit);
    expect(noEdit.text()).toContain(zhTW.dynamic.delete);
    const noDelete = await mountPage(schema({ permissions: { ...all, delete: false } }));
    expect(noDelete.text()).toContain(zhTW.dynamic.edit);
    expect(noDelete.text()).not.toContain(zhTW.dynamic.delete);
  });

  it('renders without a heading when titleFieldKey is null', async () => {
    const w = await mountPage(schema({ titleFieldKey: null, blocks: [{ id: 1, label: { 'zh-TW': '區' }, collapsed: false, fields: [field(2, '甲')] }] }));
    expect(w.text()).toContain('甲');
    expect(w.text()).not.toContain('記錄五');
  });

  it('does not show fields hidden by a rule', async () => {
    const hidden = { kind: 'visibility' as const, condition: { field: 1, op: 'eq' as const, value: 'nope' }, targets: ['field:2'], options: [] };
    const w = await mountPage(schema({ rules: [hidden] }));
    expect(w.text()).toContain('標題');
    expect(w.text()).not.toContain('甲');
  });

  it('still applies a Visibility rule on a formula field (stored values carry server-computed formulas)', async () => {
    const total: SchemaField = { ...field(3, '合計'), type: 'formula' };
    const hide = { kind: 'visibility' as const, condition: { field: 3, op: 'lte' as const, value: 100 }, targets: ['field:2'], options: [] };
    const s = schema({ rules: [hide], blocks: [{ id: 1, label: { 'zh-TW': '區' }, collapsed: false, fields: [field(1, '標題'), field(2, '甲'), total] }] });
    const base = record.values;
    record.values = { ...base, f_3: 500 }; // mountPage serves the shared `record`
    const w = await mountPage(s);
    expect(w.text()).not.toContain('甲'); // condition (total <= 100) does not hold -> hidden
    record.values = { ...base, f_3: 50 };
    const w2 = await mountPage(s);
    record.values = base;
    expect(w2.text()).toContain('甲');
  });

  describe('delete conflicts', () => {
    async function clickDelete(w: Awaited<ReturnType<typeof mountPage>>) {
      const del = w.findAll('button').find((b) => b.text() === zhTW.dynamic.delete)!;
      await del.trigger('click');
      await flushPromises();
      const confirm = Array.from(document.querySelectorAll('.q-dialog button')).find((b) => b.textContent?.trim() === zhTW.dynamic.delete) as HTMLElement;
      confirm.click();
      await flushPromises();
    }

    it('delete 409 with references opens blocked dialog', async () => {
      api.deleteRecord.mockRejectedValue({ status: 409, message: '還有其他資料參照這筆記錄，不能刪除', total: 1,
        references: [{ moduleKey: 'orders', moduleName: { 'zh-TW': '訂單' }, fieldLabel: { 'zh-TW': '客戶' }, recordId: 3, title: '單三' }] });
      const w = await mountPage(schema({}));
      await clickDelete(w);
      expect(document.querySelector('[data-testid="blocked-message"]')!.textContent).toContain('還有其他資料參照');
      expect(document.querySelectorAll('[data-testid="blocked-item"]')).toHaveLength(1);
    });

    it('delete 409 depth message shows notify', async () => {
      api.deleteRecord.mockRejectedValue({ status: 409, message: '連帶刪除超過 5 層，請先手動刪除下層資料' });
      const w = await mountPage(schema({}));
      await clickDelete(w);
      expect(document.querySelector('[data-testid="blocked-message"]')).toBeNull();
      await vi.waitFor(() => expect(document.body.textContent).toContain('連帶刪除超過 5 層'));
      expect(document.body.textContent).not.toContain(zhTW.dynamic.stale);
    });

    it('delete 409 stale shows stale notify with reload', async () => {
      api.deleteRecord.mockRejectedValue({ status: 409, message: '資料已被其他人修改，請重新載入' });
      const w = await mountPage(schema({}));
      await clickDelete(w);
      await vi.waitFor(() => expect(document.body.textContent).toContain(zhTW.dynamic.stale));
      expect(document.body.textContent).toContain(zhTW.dynamic.reload);
    });
  });

  describe('references', () => {
    const refField = (id: number, kind: 'module' | 'entity'): SchemaField => ({
      ...field(id, `參照${id}`), type: 'reference',
      reference: { kind, key: kind === 'module' ? 'cust' : 'design.mold', name: { 'zh-TW': 'x' }, lightUrl: '/x' },
    });
    const withRefs = (fields: SchemaField[], values: Record<string, unknown>, references: Record<string, unknown>, extra: Partial<ModuleSchema> = {}) =>
      [schema({ blocks: [{ id: 1, label: { 'zh-TW': '區' }, collapsed: false, fields: [field(1, '標題'), ...fields] }], ...extra }),
        { ...record, values: { f_1: 'a', ...values }, references } as DynamicRecord] as const;

    it('reference ok renders link to target record', async () => {
      const [s, r] = withRefs([refField(2, 'module'), refField(3, 'entity')], { f_2: 42, f_3: 'M-1' }, {
        f_2: { key: '42', title: '客戶甲', state: 'ok' }, f_3: { key: 'M-1', title: 'M-1 模具', state: 'ok' },
      });
      const w = await mountPage(s, r);
      const link = w.find('[data-testid="f_2"] a');
      expect(link.text()).toBe('客戶甲');
      expect(link.attributes('href')).toBe('/dynamic/cust/42');
      expect(w.find('[data-testid="f_3"]').text()).toBe('M-1 模具');
      expect(w.find('[data-testid="f_3"] a').exists()).toBe(false);
    });

    it('reference missing shows 已失效', async () => {
      const [s, r] = withRefs([refField(2, 'module')], { f_2: 42 }, { f_2: { key: '42', state: 'missing' } });
      const w = await mountPage(s, r);
      expect(w.find('[data-testid="f_2"]').text()).toBe(zhTW.dynamic.reference.missing);
      expect(w.find('[data-testid="f_2"] a').exists()).toBe(false);
    });

    it('reference restricted shows 無權限檢視', async () => {
      const [s, r] = withRefs([refField(2, 'module')], { f_2: 42 }, { f_2: { key: '42', title: null, state: 'restricted' } });
      const w = await mountPage(s, r);
      expect(w.find('[data-testid="f_2"]').text()).toBe(zhTW.dynamic.reference.restricted);
      expect(w.find('[data-testid="f_2"] a').exists()).toBe(false);
    });

    it('extension record shows host title', async () => {
      const [s, r] = withRefs([], {}, {}, { kind: 'extension', hostEntityKey: 'design.mold', titleFieldKey: null });
      const w = await mountPage(s, { ...r, title: 'M-0001 外殼上蓋模', hostKey: 'M-0001' });
      expect(w.find('.text-h6').text()).toBe('M-0001 外殼上蓋模');
    });
  });

  it('details render between fields and related tabs', async () => {
    const line: SchemaDetail = {
      moduleKey: 'line', name: { 'zh-TW': '訂單明細' }, parentFieldId: 9, parentFieldKey: 'f_9', titleFieldKey: 'f_7',
      fields: [field(7, '品名')], rules: [],
    };
    const lineRow: DynamicRecord = { ...record, id: 11, moduleKey: 'line', values: { f_7: '螺絲' } };
    const tab = { fieldId: 5, moduleKey: 'o', moduleName: { 'zh-TW': '關聯模組' }, fieldLabel: { 'zh-TW': '欄' }, canAdd: false };
    api.relatedPaged = vi.fn().mockResolvedValue({ items: [], totalCount: 0 });
    const w = await mountPage(schema({ details: [line] }), { ...record, details: { line: [lineRow] } }, [tab]);
    const html = w.html();
    expect(html).toContain('螺絲');
    expect(html.indexOf('data-testid="f_2"')).toBeLessThan(html.indexOf('訂單明細'));
    expect(html.indexOf('訂單明細')).toBeLessThan(html.indexOf('data-testid="related-tabs"'));
  });
});
