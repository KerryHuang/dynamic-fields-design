import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { ModuleEvent } from 'src/domain/dynamic/events-ledger';
import type { DesignerField, ModuleDetail } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import EventsTab from './EventsTab.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: {
    getEvents: vi.fn(), deleteEvent: vi.fn(), getMe: vi.fn(), getWorkflow: vi.fn(), getModules: vi.fn(), getModule: vi.fn(), getModulePermissions: vi.fn(),
  },
}));
const api = vi.mocked(dynamicApi);

const status: DesignerField = {
  id: 5, key: 'f_5', type: 'enum', label: { 'zh-TW': '狀態' }, blockId: 1, sequence: 1, status: 'Active',
  config: { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false },
  options: [11, 12].map((n) => ({ id: n, value: `opt_${n}`, label: { 'zh-TW': `狀態${n}` }, sequence: n, active: true })),
};
const mod = (extra: Partial<ModuleDetail> = {}): ModuleDetail => ({
  id: 1, key: 'ship', programCode: 'P', name: { 'zh-TW': '出貨' }, status: 'Active', version: 1,
  blocks: [{ id: 1, label: { 'zh-TW': 'b' }, sequence: 1, collapsed: false, columns: 2, fields: [status] }], ...extra,
});
const event = (extra: Partial<ModuleEvent> = {}): ModuleEvent => ({
  key: 'ship-post', name: { 'zh-TW': '出貨過帳' }, sourceModuleKey: 'ship', active: true,
  definition: { trigger: { on: 'transition', fromStates: [11], toState: 12 }, effects: [{ code: 'a', kind: 'custom' }, { code: 'b', kind: 'custom' }] }, ...extra,
});

async function mountTab(events: ModuleEvent[], m = mod(), canEdit = true) {
  api.getEvents.mockResolvedValue(events);
  api.getMe.mockResolvedValue({ designer: { read: true, add: true, edit: canEdit, delete: true } } as never);
  api.getWorkflow.mockResolvedValue({ statusFieldId: 5, initialOptionId: 11, states: [{ optionId: 11, readOnly: false }, { optionId: 12, readOnly: false }], transitions: [] });
  api.getModules.mockResolvedValue([]);
  api.getModulePermissions.mockResolvedValue({ groups: [], users: [], options: [], fields: [] });
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(EventsTab, {
    props: { module: m },
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], createPinia(), i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
    attachTo: document.body,
  });
  await flushPromises();
  return w;
}
const $ = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

describe('EventsTab', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    document.querySelectorAll('.q-dialog').forEach((el) => el.remove());
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  it('lists the events of this module with what triggers them and how many effects they have', async () => {
    const w = await mountTab([event(), event({ key: 'off', name: { 'zh-TW': '停用的' }, active: false, definition: { trigger: { on: 'create' }, effects: [] } })]);
    expect(api.getEvents).toHaveBeenCalledWith('ship');
    const row = w.find('[data-testid="event-row-ship-post"]').text();
    expect(row).toContain('出貨過帳');
    expect(row).toContain('狀態轉移');
    expect(row).toContain('狀態11 → 狀態12');
    expect(row).toContain('2 個效果');
    expect(w.find('[data-testid="event-row-off"]').text()).toContain(zhTW.events.off);
  });

  it('says so when there are no events, and opens the editor to add one', async () => {
    const w = await mountTab([]);
    expect(w.text()).toContain(zhTW.events.empty);
    await w.find('[data-testid="event-create"]').trigger('click');
    await flushPromises();
    expect($('event-save')).not.toBeNull();
  });

  it('opens an existing event with its code locked', async () => {
    const w = await mountTab([event()]);
    await w.find('[data-testid="event-row-ship-post"]').trigger('click');
    await flushPromises();
    expect(($('event-key')!.querySelector('input') as HTMLInputElement).readOnly).toBe(true);
  });

  it('deletes after confirming', async () => {
    api.deleteEvent.mockResolvedValue(undefined as never);
    const w = await mountTab([event()]);
    await w.find('[data-testid="event-delete-ship-post"]').trigger('click');
    await flushPromises();
    ($('event-delete-confirm') as HTMLElement).click();
    await flushPromises();
    expect(api.deleteEvent).toHaveBeenCalledWith('ship-post');
  });

  it('detail and extension modules cannot be an event source', async () => {
    const w = await mountTab([], mod({ detailOf: 'order' }));
    expect(w.find('[data-testid="events-unsupported"]').exists()).toBe(true);
    expect(w.find('[data-testid="event-create"]').exists()).toBe(false);
    expect(api.getEvents).not.toHaveBeenCalled();
  });

  it('without designer edit permission the buttons are disabled', async () => {
    const w = await mountTab([event()], mod(), false);
    expect(w.find('[data-testid="event-create"]').attributes('disabled')).toBeDefined();
    expect(w.find('[data-testid="event-delete-ship-post"]').attributes('disabled')).toBeDefined();
    expect(w.find('[data-testid="event-row-ship-post"]').classes()).not.toContain('q-item--clickable');
    await w.find('[data-testid="event-row-ship-post"]').trigger('click');
    await flushPromises();
    expect($('event-key')).toBeNull();
  });

  it('the delete button does not open the editor', async () => {
    const w = await mountTab([event()]);
    expect(w.find('[data-testid="event-row-ship-post"]').classes()).toContain('q-item--clickable');
    await w.find('[data-testid="event-delete-ship-post"]').trigger('click');
    await flushPromises();
    expect($('event-key')).toBeNull();
    expect($('event-delete-confirm')).not.toBeNull();
  });
});
