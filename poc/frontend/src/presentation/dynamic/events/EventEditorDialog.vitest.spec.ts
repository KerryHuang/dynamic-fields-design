import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { ModuleEvent } from 'src/domain/dynamic/events-ledger';
import type { DesignerField, FieldType, ModuleDetail } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { findInputByTestId } from 'src/test-utils/find-by-testid';
import { dynamicApi, type ModuleSummary } from 'src/services/api/dynamic-api';
import EventEditorDialog from './EventEditorDialog.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: {
    createEvent: vi.fn(), updateEvent: vi.fn(), getModule: vi.fn(), getModules: vi.fn(), getEvents: vi.fn(), getWorkflow: vi.fn(), getModulePermissions: vi.fn(),
  },
}));
const api = vi.mocked(dynamicApi);

const opt = (id: number) => ({ id, value: `opt_${id}`, label: { 'zh-TW': `狀態${id}` }, sequence: id, active: true });
const field = (id: number, type: FieldType, extra: Partial<DesignerField['config']> = {}, options: DesignerField['options'] = []): DesignerField => ({
  id, key: `f_${id}`, type, label: { 'zh-TW': `欄${id}` }, blockId: 1, sequence: id, status: 'Active', options,
  config: { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false, ...extra },
});
const mod = (key: string, fields: DesignerField[]): ModuleDetail => ({
  id: 1, key, programCode: 'P', name: { 'zh-TW': key }, status: 'Active', version: 1,
  blocks: [{ id: 1, label: { 'zh-TW': 'b' }, sequence: 1, collapsed: false, columns: 2, fields }],
});
const summary = (key: string, extra: Partial<ModuleSummary> = {}): ModuleSummary => ({
  id: 1, key, programCode: 'P', name: { 'zh-TW': key }, status: 'Active', version: 1, ...extra,
});

const ship = mod('ship', [field(5, 'enum', {}, [opt(11), opt(12)]), field(22, 'text'), field(33, 'reference', { refTarget: { kind: 'module', key: 'balance' } })]);
const details: Record<string, ModuleDetail> = {
  'ship-line': mod('ship-line', [field(41, 'integer'), field(42, 'decimal')]),
  'stock-move': mod('stock-move', [field(71, 'integer'), field(72, 'integer'), field(74, 'enum', {}, [opt(55)]), field(75, 'formula')]),
  balance: mod('balance', [field(61, 'date')]),
};

const created: ModuleEvent = {
  key: 'ship-post', name: { 'zh-TW': '出貨過帳' }, sourceModuleKey: 'ship', active: true,
  definition: {
    trigger: { on: 'transition', fromStates: [11], toState: 12 },
    effects: [
      { code: 'post', kind: 'create', targetModuleKey: 'stock-move', each: 'ship-line', set: [{ to: 71, from: { side: 'line', fieldId: 41 }, allowHiddenSource: false }] },
      { code: 'ext', kind: 'custom', handler: 'notify', args: { x: 1 } },
    ],
  },
};

const mounted: { unmount: () => void }[] = [];
async function open(event?: ModuleEvent, opts: { workflow?: boolean; hidden?: number[] } = {}) {
  api.getModules.mockResolvedValue([summary('ship'), summary('ship-line', { detailOf: 'ship' }), summary('stock-move'), summary('balance')]);
  api.getModule.mockImplementation(async (k: string) => details[k]!);
  api.getEvents.mockResolvedValue(event ? [event] : []);
  api.getWorkflow.mockImplementation(async () => {
    if (opts.workflow === false) throw { status: 404 };
    return { statusFieldId: 5, initialOptionId: 11, states: [{ optionId: 11, readOnly: false }, { optionId: 12, readOnly: false }], transitions: [] };
  });
  api.getModulePermissions.mockImplementation(async () => ({
    groups: [], users: [], options: [], fields: (opts.hidden ?? []).map((fieldId) => ({ fieldId, groupId: 1, access: 'hidden' as const })),
  }));
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(EventEditorDialog, {
    props: { modelValue: true, module: ship, event },
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], createPinia(), i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
    attachTo: document.body,
  });
  mounted.push(w);
  await flushPromises();
  return w;
}
type W = Awaited<ReturnType<typeof open>>;
const $ = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
const type = async (selector: string, value: string) => {
  const el = document.body.querySelector(selector) as HTMLInputElement;
  el.value = value;
  el.dispatchEvent(new Event('input'));
  await flushPromises();
};
const comp = (w: W, id: string) => findInputByTestId(w, id);
const emit = async (w: W, id: string, v: unknown) => { comp(w, id).vm.$emit('update:modelValue', v); await flushPromises(); };
const click = async (id: string) => { $(id)!.click(); await flushPromises(); };
const saveBtn = () => $('event-save') as HTMLButtonElement;
const values = (w: W, id: string) => (comp(w, id).props('options') as { value: unknown }[]).map((o) => o.value);

describe('EventEditorDialog', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    for (const w of mounted.splice(0)) w.unmount();
    document.querySelectorAll('.q-dialog').forEach((el) => el.remove());
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  it('builds an event from the form: trigger, a creating effect over detail rows, and a mapping', async () => {
    api.createEvent.mockResolvedValue(created);
    const w = await open();
    expect(saveBtn().disabled).toBe(true);
    await type('[data-testid="event-key"] input', 'ship-post');
    await type('[data-testid="event-name"] [data-testid="i18n-zh-TW"] input', '出貨過帳');
    await emit(w, 'event-trigger', 'transition');
    await emit(w, 'event-from-states', [11]);
    await emit(w, 'event-to-state', 12);
    await click('event-add-effect');
    await emit(w, 'effect-0-target', 'stock-move');
    expect(values(w, 'effect-0-each')).toEqual(['ship-line']);
    await emit(w, 'effect-0-each', 'ship-line');
    await click('effect-0-set-add');
    await emit(w, 'set-0-0-to', 71);
    await emit(w, 'set-0-0-side', 'line');
    expect(values(w, 'set-0-0-field')).toEqual([41, 42]);
    await emit(w, 'set-0-0-field', 41);
    saveBtn().click();
    await flushPromises();
    expect(api.createEvent).toHaveBeenCalledWith({
      key: 'ship-post', name: { 'zh-TW': '出貨過帳' }, sourceModuleKey: 'ship', active: true,
      definition: {
        trigger: { on: 'transition', fromStates: [11], toState: 12 },
        effects: [{ code: 'effect-1', kind: 'create', targetModuleKey: 'stock-move', each: 'ship-line', set: [{ to: 71, from: { side: 'line', fieldId: 41 }, allowHiddenSource: false }] }],
      },
    });
  });

  it('target fields exclude formulas; the update effect offers only references to its target', async () => {
    const w = await open();
    await click('event-add-effect');
    await emit(w, 'effect-0-target', 'stock-move');
    await click('effect-0-set-add');
    expect(values(w, 'set-0-0-to')).toEqual([71, 72, 74]);
    await emit(w, 'effect-0-kind', 'update');
    await emit(w, 'effect-0-target', 'balance');
    expect(values(w, 'effect-0-via')).toEqual([33]);
  });

  it('a transition needs a state machine on the source', async () => {
    const w = await open(undefined, { workflow: false });
    const options = comp(w, 'event-trigger').props('options') as { value: string; disable?: boolean }[];
    expect(options.find((o) => o.value === 'transition')!.disable).toBe(true);
  });

  it('a reverse effect picks an event and one of its creating effects', async () => {
    const w = await open(created);
    await click('event-add-effect');
    await emit(w, 'effect-2-kind', 'reverse');
    await emit(w, 'effect-2-target', 'stock-move');
    expect(values(w, 'effect-2-of-event')).toEqual(['ship-post']);
    await emit(w, 'effect-2-of-event', 'ship-post');
    expect(values(w, 'effect-2-of-effect')).toEqual(['post']);
  });

  it('editing: the code of the event and of its saved effects is fixed, a custom effect is view-only but kept', async () => {
    api.updateEvent.mockResolvedValue(created);
    const w = await open(created);
    expect(($('event-key')!.querySelector('input') as HTMLInputElement).readOnly).toBe(true);
    expect((comp(w, 'effect-0-code').vm.$el.querySelector('input') as HTMLInputElement).readOnly).toBe(true);
    expect($('effect-1-custom')!.textContent).toContain('notify');
    expect($('effect-1-kind')).toBeNull();
    await click('event-add-effect');
    expect((comp(w, 'effect-2-code').vm.$el.querySelector('input') as HTMLInputElement).readOnly).toBe(false);
    await click('effect-2-remove');
    saveBtn().click();
    await flushPromises();
    expect(api.updateEvent).toHaveBeenCalledWith('ship-post', created);
  });

  it('asks for consent when a mapping reads a hidden source field, and sends it once given', async () => {
    api.createEvent.mockResolvedValue(created);
    const w = await open(undefined, { hidden: [22] });
    await type('[data-testid="event-key"] input', 'e1');
    await type('[data-testid="event-name"] [data-testid="i18n-zh-TW"] input', 'x');
    await click('event-add-effect');
    await emit(w, 'effect-0-target', 'stock-move');
    await click('effect-0-set-add');
    await emit(w, 'set-0-0-to', 72);
    await emit(w, 'set-0-0-field', 22);
    expect($('set-0-0-consent')!.textContent).toContain('允許複製被隱藏的值');
    await emit(w, 'set-0-0-allow-hidden', true);
    saveBtn().click();
    await flushPromises();
    expect(api.createEvent.mock.calls[0]![0].definition.effects[0]!.set).toEqual([{ to: 72, from: { side: 'header', fieldId: 22 }, allowHiddenSource: true }]);
  });

  it('shows server messages on the effect and mapping they belong to, and the rest below', async () => {
    api.updateEvent.mockRejectedValue({
      status: 400, errors: {
        'definition.effects[0].set[0].to': ['目的欄位型別不相容'], 'definition.effects[0].targetModuleKey': ['目標模組沒有啟用'],
        'definition.effects[1]': ['循環：ship-post → stock-move → ship-post'], name: ['名稱重複'],
      },
    });
    await open(created);
    saveBtn().click();
    await flushPromises();
    expect($('set-0-0-error')!.textContent).toContain('目的欄位型別不相容');
    expect($('effect-0-errors')!.textContent).toContain('目標模組沒有啟用');
    expect($('effect-1-errors')!.textContent).toContain('循環');
    expect($('event-name')!.textContent).toContain('名稱重複');
  });

  it('shows the warnings of a saved event', async () => {
    api.createEvent.mockResolvedValue({ ...created, warnings: ['目標模組有必填欄位沒有對應'] });
    const w = await open();
    await type('[data-testid="event-key"] input', 'e1');
    await type('[data-testid="event-name"] [data-testid="i18n-zh-TW"] input', 'x');
    saveBtn().click();
    await flushPromises();
    await vi.waitFor(() => expect(document.body.querySelector('.q-notifications')?.textContent).toContain('目標模組有必填欄位沒有對應'));
    expect(w.emitted('update:modelValue')?.at(-1)).toEqual([false]);
  });

  it('stops adding effects at ten', async () => {
    const many: ModuleEvent = {
      ...created, definition: { trigger: { on: 'create' }, effects: Array.from({ length: 10 }, (_, i) => ({ code: `e${i}`, kind: 'custom' as const, handler: 'h' })) },
    };
    await open(many);
    expect(($('event-add-effect') as HTMLButtonElement).disabled).toBe(true);
  });
});
