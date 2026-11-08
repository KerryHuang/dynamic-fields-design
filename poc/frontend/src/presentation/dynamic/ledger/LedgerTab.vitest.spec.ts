import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { LedgerConfig } from 'src/domain/dynamic/events-ledger';
import type { DesignerField, FieldType, ModuleDetail } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { findInputByTestId } from 'src/test-utils/find-by-testid';
import { dynamicApi } from 'src/services/api/dynamic-api';
import LedgerTab from './LedgerTab.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: {
    getLedger: vi.fn(), putLedger: vi.fn(), deleteLedger: vi.fn(), getWorkflow: vi.fn(), getModules: vi.fn(), getModule: vi.fn(), getMe: vi.fn(), addField: vi.fn(),
  },
}));
const api = vi.mocked(dynamicApi);

const field = (id: number, type: FieldType, extra: Partial<DesignerField['config']> = {}): DesignerField => ({
  id, key: `f_${id}`, type, label: { 'zh-TW': `欄${id}` }, blockId: 1, sequence: id, status: 'Active', options: [],
  config: { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false, ...extra },
});
const self = { refTarget: { kind: 'module' as const, key: 'journal' }, readOnly: true };
const mod = (extra: Partial<ModuleDetail> = {}, fields = [field(1, 'text'), field(2, 'decimal'), field(3, 'decimal'), field(4, 'reference', self)]): ModuleDetail => ({
  id: 1, key: 'journal', programCode: 'P', name: { 'zh-TW': '傳票' }, status: 'Active', version: 1,
  blocks: [{ id: 7, label: { 'zh-TW': 'b' }, sequence: 1, collapsed: false, columns: 2, fields }], ...extra,
});
const saved = (extra: Partial<LedgerConfig> = {}): LedgerConfig => ({ moduleKey: 'journal', reversalFieldId: 4, negate: [2], swap: [], recordCount: 0, ...extra });

async function mountTab(m: ModuleDetail, existing: LedgerConfig | null, workflow = false) {
  api.getLedger.mockImplementation(async () => {
    if (existing) return existing;
    throw { status: 404 };
  });
  api.getWorkflow.mockImplementation(async () => {
    if (workflow) return { statusFieldId: 1, initialOptionId: 1, states: [], transitions: [] };
    throw { status: 404 };
  });
  api.getModules.mockResolvedValue([]);
  api.getMe.mockResolvedValue({ designer: { read: true, add: true, edit: true, delete: true } } as never);
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(LedgerTab, {
    props: { module: m },
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], createPinia(), i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
    attachTo: document.body,
  });
  await flushPromises();
  return w;
}
const comp = (w: Awaited<ReturnType<typeof mountTab>>, testid: string) => findInputByTestId(w, testid);
const pick = async (w: Awaited<ReturnType<typeof mountTab>>, testid: string, value: unknown) => {
  comp(w, testid).vm.$emit('update:modelValue', value);
  await flushPromises();
};
const body = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

describe('LedgerTab', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    document.querySelectorAll('.q-dialog').forEach((el) => el.remove());
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  it('turning the switch on offers the reversal field, and saving needs one', async () => {
    api.putLedger.mockResolvedValue(saved());
    const w = await mountTab(mod(), null);
    expect(w.find('[data-testid="ledger-reversal-field"]').exists()).toBe(false);
    await pick(w, 'ledger-enable', true);
    expect(w.find('[data-testid="ledger-save"]').attributes('disabled')).toBeDefined();
    await pick(w, 'ledger-reversal-field', 4);
    await pick(w, 'ledger-negate', [2]);
    await w.find('[data-testid="ledger-save"]').trigger('click');
    await flushPromises();
    expect(api.putLedger).toHaveBeenCalledWith('journal', { reversalFieldId: 4, negate: [2], swap: [], confirmFreeze: false });
  });

  it('lists only read-only self references as reversal fields', async () => {
    const w = await mountTab(mod(), null);
    await pick(w, 'ledger-enable', true);
    const options = comp(w, 'ledger-reversal-field').props('options') as { value: number }[];
    expect(options.map((o) => o.value)).toEqual([4]);
  });

  it('the first save of a module that has records asks for confirmation with the count, then resends confirmed', async () => {
    api.putLedger.mockRejectedValueOnce({ status: 409, message: '這個模組已有 12 筆記錄，設為分錄後它們會立刻變成不可修改；確認請帶 confirmFreeze' }).mockResolvedValue(saved({ recordCount: 12 }));
    const w = await mountTab(mod(), null);
    await pick(w, 'ledger-enable', true);
    await pick(w, 'ledger-reversal-field', 4);
    await w.find('[data-testid="ledger-save"]').trigger('click');
    await flushPromises();
    const dialog = body('ledger-confirm')!;
    expect(dialog.textContent).toContain('12 筆');
    expect(dialog.textContent).toContain('無法取消');
    expect(dialog.textContent).not.toContain('confirmFreeze');
    (body('ledger-confirm-ok') as HTMLElement).click();
    await flushPromises();
    expect(api.putLedger).toHaveBeenCalledTimes(2);
    expect(api.putLedger.mock.calls[1]![1]).toMatchObject({ reversalFieldId: 4, confirmFreeze: true });
  });

  it('a ledger that already has records is shown locked and cannot be turned off', async () => {
    const w = await mountTab(mod(), saved({ recordCount: 3 }));
    expect(w.find('[data-testid="ledger-enable"]').attributes('aria-disabled')).toBe('true');
    expect(w.find('[data-testid="ledger-locked"]').text()).toContain('3 筆');
    // the reversal rules are frozen with the records, so there is nothing left to save
    expect(w.find('[data-testid="ledger-save"]').attributes('disabled')).toBeDefined();
    expect(comp(w, 'ledger-reversal-field').props('disable')).toBe(true);
    expect(comp(w, 'ledger-negate').props('disable')).toBe(true);
  });

  it('an empty ledger can be removed again', async () => {
    api.deleteLedger.mockResolvedValue(undefined as never);
    const w = await mountTab(mod(), saved());
    await pick(w, 'ledger-enable', false);
    await w.find('[data-testid="ledger-save"]').trigger('click');
    await flushPromises();
    expect(api.deleteLedger).toHaveBeenCalledWith('journal');
  });

  it('explains and disables the switch when the module has a state machine', async () => {
    const w = await mountTab(mod(), null, true);
    expect(w.find('[data-testid="ledger-has-workflow"]').exists()).toBe(true);
    expect(w.find('[data-testid="ledger-enable"]').attributes('aria-disabled')).toBe('true');
  });

  it('detail and extension modules cannot be ledgers', async () => {
    const w = await mountTab(mod({ detailOf: 'order' }), null);
    expect(w.find('[data-testid="ledger-unsupported"]').exists()).toBe(true);
    expect(w.find('[data-testid="ledger-enable"]').exists()).toBe(false);
  });

  it('creates the reversal field through the add-field API and selects it', async () => {
    api.addField.mockResolvedValue(field(9, 'reference', self));
    const w = await mountTab(mod({}, [field(1, 'text'), field(2, 'decimal')]), null);
    await pick(w, 'ledger-enable', true);
    expect(w.find('[data-testid="ledger-no-candidate"]').exists()).toBe(true);
    await w.find('[data-testid="ledger-create-reversal"]').trigger('click');
    await flushPromises();
    expect(api.addField).toHaveBeenCalledWith('journal', {
      blockId: 7, type: 'reference', label: expect.objectContaining({ 'zh-TW': '沖銷自' }),
      config: expect.objectContaining({ readOnly: true, required: false, refTarget: { kind: 'module', key: 'journal' } }),
    });
    expect(comp(w, 'ledger-reversal-field').props('modelValue')).toBe(9);
  });

  it('pairs swap fields of the same type only', async () => {
    api.putLedger.mockResolvedValue(saved());
    const w = await mountTab(mod({}, [field(2, 'decimal'), field(3, 'decimal'), field(5, 'integer'), field(4, 'reference', self)]), null);
    await pick(w, 'ledger-enable', true);
    await pick(w, 'ledger-reversal-field', 4);
    await w.find('[data-testid="ledger-swap-add"]').trigger('click');
    await pick(w, 'ledger-swap-a-0', 2);
    const second = comp(w, 'ledger-swap-b-0').props('options') as { value: number }[];
    expect(second.map((o) => o.value)).toEqual([3]);
    await pick(w, 'ledger-swap-b-0', 3);
    await w.find('[data-testid="ledger-save"]').trigger('click');
    await flushPromises();
    expect(api.putLedger.mock.calls[0]![1]).toMatchObject({ swap: [[2, 3]] });
  });

  it('shows the server messages when saving fails', async () => {
    api.putLedger.mockRejectedValue({ status: 400, errors: { negate: ['取負號欄位必須是數字欄位'] } });
    const w = await mountTab(mod(), saved());
    await w.find('[data-testid="ledger-save"]').trigger('click');
    await flushPromises();
    expect(w.find('[data-testid="ledger-errors"]').text()).toContain('取負號欄位必須是數字欄位');
  });
});
