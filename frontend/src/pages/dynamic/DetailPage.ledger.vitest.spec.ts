import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import type { RecordEffects, RecordLedger } from 'src/domain/dynamic/events-ledger';
import type { DynamicRecord, ModuleSchema, SchemaField, SchemaWorkflow } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import DetailPage from './DetailPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: {
    getModuleSchema: vi.fn(), getRecord: vi.fn(), related: vi.fn(), recordHistory: vi.fn(), getRecordConversions: vi.fn(),
    getRecordLedger: vi.fn(), getRecordEffects: vi.fn(), reverseRecord: vi.fn(),
  },
}));
const api = vi.mocked(dynamicApi);

const base = { required: false, readOnly: false, showInList: true, isFilterable: true, isSortable: true };
const status: SchemaField = {
  id: 5, key: 'f_5', type: 'enum', label: { 'zh-TW': '狀態' }, config: base,
  options: [11, 12, 13].map((n) => ({ value: `opt_${n}`, label: { 'zh-TW': `s${n}` }, active: true })),
};
const workflow: SchemaWorkflow = {
  statusFieldKey: 'f_5', initialState: 'opt_11',
  states: [{ value: 'opt_11', readOnly: false }, { value: 'opt_12', readOnly: false }, { value: 'opt_13', readOnly: true }],
  transitions: [{ from: 'opt_11', to: 'opt_12' }],
};
const all = { read: true, add: true, edit: true, delete: true, printExport: true, pricing: true };
const schema = (extra: Partial<ModuleSchema> = {}): ModuleSchema => ({
  key: 'm', programCode: 'DYN_M', name: { 'zh-TW': '模組' }, titleFieldKey: 'f_1', version: 1, rules: [], permissions: all,
  blocks: [{ id: 1, label: { 'zh-TW': '區' }, collapsed: false, columns: 2, fields: [
    { id: 1, key: 'f_1', type: 'text', label: { 'zh-TW': '標題' }, options: [], config: base }, status,
  ] }], ...extra,
});
const record = (extra: Partial<DynamicRecord> = {}): DynamicRecord => ({
  id: 5, moduleKey: 'm', title: '記錄五', values: { f_1: '記錄五', f_5: 'opt_11' }, ownerUserId: 'a', createdUserId: 'a', createdTime: '',
  modifiedUserId: 'a', modifiedTime: '', rowVersion: 'rv', references: {}, hostKey: null, ...extra,
});

async function mountPage(s: ModuleSchema, rec: DynamicRecord, ledger?: RecordLedger, effects?: RecordEffects) {
  setActivePinia(createPinia());
  api.getModuleSchema.mockResolvedValue(s);
  api.getRecord.mockResolvedValue(rec);
  api.related.mockResolvedValue([]);
  api.recordHistory.mockResolvedValue({ items: [], totalCount: 0, currentPage: 1, pagesSize: 20 });
  api.getRecordConversions.mockResolvedValue([]);
  api.getRecordLedger.mockResolvedValue(ledger ?? { reversible: false, reversedBy: null, reverses: null });
  api.getRecordEffects.mockResolvedValue(effects ?? { items: [], hiddenCount: 0 });
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/dynamic/:moduleKey/:id', component: DetailPage }] });
  await router.push('/dynamic/m/5');
  const push = vi.spyOn(router, 'push');
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const Host = { render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(DetailPage))) };
  const w = mount(Host, {
    global: {
      plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], router, i18n,
        [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]],
    },
  });
  await flushPromises();
  return { w, push };
}
const $ = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

describe('DetailPage ledger', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    document.querySelectorAll('.q-dialog').forEach((el) => el.remove());
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  it('a ledger module shows no edit or delete (the schema flags are off) and says why', async () => {
    const { w } = await mountPage(schema({ ledger: true, permissions: { ...all, edit: false, delete: false } }), record());
    const buttons = w.findAll('button, a').map((b) => b.text());
    expect(buttons).not.toContain(zhTW.dynamic.edit);
    expect(buttons).not.toContain(zhTW.dynamic.delete);
    expect(w.find('[data-testid="record-ledger"]').text()).toContain(zhTW.ledger.entryNotice);
  });

  it('a module that is not a ledger does not even ask', async () => {
    await mountPage(schema(), record());
    expect(api.getRecordLedger).not.toHaveBeenCalled();
  });

  it('offers to reverse a reversible entry, asks first, and goes to the new entry', async () => {
    api.reverseRecord.mockResolvedValue(record({ id: 9 }));
    const { w, push } = await mountPage(schema({ ledger: true }), record(), { reversible: true, reversedBy: null, reverses: null });
    expect(api.getRecordLedger).toHaveBeenCalledWith(5);
    await w.find('[data-testid="reverse-open"]').trigger('click');
    await flushPromises();
    expect(api.reverseRecord).not.toHaveBeenCalled();
    expect($('reverse-dialog')!.textContent).toContain(zhTW.ledger.reverse.confirm);
    $('reverse-confirm')!.click();
    await flushPromises();
    expect(api.reverseRecord).toHaveBeenCalledWith(5, {}, expect.stringMatching(/^[A-Za-z0-9_-]{8,64}$/));
    expect(push).toHaveBeenCalledWith('/dynamic/m/9');
  });

  it('a reverse retried after a failure carries the same Idempotency-Key', async () => {
    api.reverseRecord.mockRejectedValueOnce({ status: 0 }).mockResolvedValue(record({ id: 9 }));
    const { w } = await mountPage(schema({ ledger: true }), record(), { reversible: true, reversedBy: null, reverses: null });
    await w.find('[data-testid="reverse-open"]').trigger('click');
    await flushPromises();
    for (let i = 0; i < 2; i++) {
      $('reverse-confirm')!.click();
      await flushPromises();
    }
    const keys = api.reverseRecord.mock.calls.map((c) => c[2]);
    expect(keys).toHaveLength(2);
    expect(keys[1]).toBe(keys[0]);
  });

  it('shows the server message when the entry cannot be reversed', async () => {
    api.reverseRecord.mockRejectedValue({ status: 409, message: '這筆分錄已經沖銷過了' });
    const { w } = await mountPage(schema({ ledger: true }), record(), { reversible: true, reversedBy: null, reverses: null });
    await w.find('[data-testid="reverse-open"]').trigger('click');
    await flushPromises();
    $('reverse-confirm')!.click();
    await flushPromises();
    await vi.waitFor(() => expect(document.body.querySelector('.q-notifications')?.textContent).toContain('這筆分錄已經沖銷過了'));
  });

  it('a reversing entry links to the original and has no reverse button', async () => {
    const { w } = await mountPage(schema({ ledger: true }), record(), { reversible: false, reversedBy: null, reverses: { id: 3, title: '原分錄' } });
    expect(w.find('[data-testid="reverse-open"]').exists()).toBe(false);
    const link = w.find('[data-testid="ledger-reverses"] a');
    expect(link.text()).toBe('原分錄');
    expect(link.attributes('href')).toBe('/dynamic/m/3');
  });

  it('a reversed entry links to its reversal and has no reverse button', async () => {
    const { w } = await mountPage(schema({ ledger: true }), record(), { reversible: false, reversedBy: { id: 8, title: '' }, reverses: null });
    expect(w.find('[data-testid="reverse-open"]').exists()).toBe(false);
    const link = w.find('[data-testid="ledger-reversed-by"] a');
    expect(link.attributes('href')).toBe('/dynamic/m/8');
    expect(link.text()).toBe('#8');
  });
});

describe('DetailPage linked results', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    document.querySelectorAll('.q-dialog').forEach((el) => el.remove());
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  const effects: RecordEffects = {
    items: [
      { eventKey: 'ship-post', effectCode: 'post', kind: 'create', targetModuleKey: 'stock-move', targetModuleName: { 'zh-TW': '庫存異動' }, targetRecordId: 31, title: '出貨 A', createdTime: '2026-11-08T09:30:00' },
      { eventKey: 'ship-post', effectCode: 'post', kind: 'create', targetModuleKey: 'stock-move', targetModuleName: { 'zh-TW': '庫存異動' }, targetRecordId: 32, title: null, createdTime: '2026-11-08T09:30:00' },
    ],
    hiddenCount: 2,
  };

  it('lists the records this one produced, linked, with a placeholder for the ones the user may not see', async () => {
    const { w } = await mountPage(schema(), record(), undefined, effects);
    expect(api.getRecordEffects).toHaveBeenCalledWith(5);
    const section = w.find('[data-testid="record-effects"]');
    expect(section.text()).toContain(zhTW.events.results.title);
    const link = section.find('a');
    expect(link.text()).toBe('出貨 A');
    expect(link.attributes('href')).toBe('/dynamic/stock-move/31');
    expect(section.findAll('a')).toHaveLength(1);
    expect(section.text()).toContain('（無權限檢視）');
    expect(section.text()).toContain('另有 2 筆你看不到的記錄');
  });

  it('shows nothing when no record was produced', async () => {
    const { w } = await mountPage(schema(), record());
    expect(w.find('[data-testid="record-effects"]').exists()).toBe(false);
  });

  it('only a count when every produced record is out of sight', async () => {
    const { w } = await mountPage(schema(), record(), undefined, { items: [], hiddenCount: 3 });
    expect(w.find('[data-testid="record-effects"]').text()).toContain('另有 3 筆你看不到的記錄');
  });

  it('a posted record cannot be deleted or edited, and says so', async () => {
    const { w } = await mountPage(schema(), record({ frozenByEffects: true }));
    expect(w.text()).not.toContain(zhTW.dynamic.delete);
    expect(w.text()).not.toContain(zhTW.dynamic.edit);
    expect(w.find('[data-testid="frozen-notice"]').text()).toContain(zhTW.ledger.frozen);
  });

  it('a posted record can still be edited while the status can move (the cancel flow)', async () => {
    const { w } = await mountPage(schema({ workflow }), record({ frozenByEffects: true }));
    expect(w.text()).toContain(zhTW.dynamic.edit);
    expect(w.text()).not.toContain(zhTW.dynamic.delete);
    const stuck = await mountPage(schema({ workflow }), record({ frozenByEffects: true, values: { f_1: 'x', f_5: 'opt_13' } }));
    expect(stuck.w.text()).not.toContain(zhTW.dynamic.edit);
  });
});
