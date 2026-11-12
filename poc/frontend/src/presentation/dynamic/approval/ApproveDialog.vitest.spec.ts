import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { DynamicRecord, ModuleSchema, SchemaField } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { bodyOf, HttpFail, mockHttp, type Call } from 'src/test-utils/mock-http';
import ApproveDialog from './ApproveDialog.vue';
import { changedValues } from './approval-actions';

const base = { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false };
const field = (id: number, label: string, type: SchemaField['type'] = 'text', config: Partial<SchemaField['config']> = {}): SchemaField =>
  ({ id, key: `f_${id}`, type, label: { 'zh-TW': label }, options: [], config: { ...base, ...config } });
const schema: ModuleSchema = {
  key: 'po', programCode: 'DYN_PO', name: { 'zh-TW': '採購單' }, titleFieldKey: 'f_1', version: 1, rules: [],
  permissions: { read: true, add: true, edit: true, delete: true, printExport: true, pricing: true },
  blocks: [{ id: 1, label: { 'zh-TW': '區' }, collapsed: false, columns: 2, fields: [field(1, '名稱'), field(2, '核定金額'), field(3, '備註')] }],
};
const record: DynamicRecord = {
  id: 9, moduleKey: 'po', title: 'abc', values: { f_1: 'abc', f_2: 'old', f_3: 'memo' }, ownerUserId: 'a', createdUserId: 'a', createdTime: '',
  modifiedUserId: 'a', modifiedTime: '', rowVersion: 'rv1', references: {}, hostKey: null,
};

let calls: Call[];
const body = () => document.body;
const q = (id: string) => body().querySelector<HTMLElement>(`[data-testid="${id}"]`);
const settle = async () => { await flushPromises(); await new Promise((r) => setTimeout(r, 20)); await flushPromises(); };
const inputOf = (label: string) => [...body().querySelectorAll<HTMLInputElement>('.q-field')].find((f) => f.textContent?.includes(label))!.querySelector('input')!;
const type = async (label: string, v: string) => { const i = inputOf(label); i.value = v; i.dispatchEvent(new Event('input')); await settle(); };
const posts = () => calls.filter((c) => c.method === 'post');

async function mountDialog(o: { keys?: string[]; reply?: unknown } = {}) {
  calls = mockHttp((c) => {
    if (c.method === 'post') return o.reply ?? { outcome: 'advanced', instanceId: 7, instanceRowVersion: 'iv2' };
    if (c.url === '/policies/effective') return { values: {} };
    return {};
  });
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(ApproveDialog, {
    attachTo: document.body,
    props: { modelValue: true, recordId: 9, moduleKey: 'po', taskId: 11, rowVersion: 'iv1', editableFieldKeys: o.keys ?? [], schema, record },
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], createPinia(), i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
  });
  await settle();
  return w;
}

describe('ApproveDialog editable fields', () => {
  beforeEach(() => { document.body.innerHTML = ''; vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} }); });
  afterEach(() => { document.body.innerHTML = ''; });

  it('shows editable fields prefilled with record values under 這一關可修改', async () => {
    const w = await mountDialog({ keys: ['f_2', 'f_3'] });
    expect(q('approve-editable')!.textContent).toContain('這一關可修改');
    expect(inputOf('核定金額').value).toBe('old');
    expect(inputOf('備註').value).toBe('memo');
    expect(q('approve-editable')!.textContent).not.toContain('名稱');
    w.unmount();
  });

  it('only changed editable values are sent', async () => {
    const w = await mountDialog({ keys: ['f_2', 'f_3'] });
    await type('核定金額', 'new');
    q('approve-send')!.click();
    await settle();
    expect(bodyOf(posts()[0])).toEqual({ rowVersion: 'iv1', values: { f_2: 'new' } });
    w.unmount();
  });

  it('unchanged editable fields send no values at all', async () => {
    const w = await mountDialog({ keys: ['f_2'] });
    q('approve-send')!.click();
    await settle();
    expect(bodyOf(posts()[0])).toEqual({ rowVersion: 'iv1' });
    w.unmount();
  });

  it('no editable keys: no editable section and values omitted', async () => {
    const w = await mountDialog();
    expect(q('approve-editable')).toBeNull();
    q('approve-send')!.click();
    await settle();
    expect(bodyOf(posts()[0])).toEqual({ rowVersion: 'iv1' });
    w.unmount();
  });

  it('400 on an editable field shows under that field, other keys on top', async () => {
    const w = await mountDialog({ keys: ['f_2'], reply: new HttpFail(400, { errors: { f_2: ['金額太大'], other: ['別的問題'] } }) });
    await type('核定金額', 'new');
    q('approve-send')!.click();
    await settle();
    expect(inputOf('核定金額').closest('.q-field')!.textContent).toContain('金額太大');
    expect(q('action-error')!.textContent).toContain('別的問題');
    expect(q('action-error')!.textContent).not.toContain('金額太大');
    w.unmount();
  });

  it('editable key missing from schema is skipped', async () => {
    const w = await mountDialog({ keys: ['f_99'] });
    expect(q('approve-editable')).toBeNull();
    q('approve-send')!.click();
    await settle();
    expect(bodyOf(posts()[0])).toEqual({ rowVersion: 'iv1' });
    w.unmount();
  });

  it('a client validation failure on an editable field blocks the request', async () => {
    const s = structuredClone(schema);
    s.blocks[0].fields[1] = field(2, '核定金額', 'text', { required: true });
    const w = await mountDialog({ keys: ['f_2'] });
    await w.setProps({ schema: s });
    await type('核定金額', '');
    q('approve-send')!.click();
    await settle();
    expect(posts()).toHaveLength(0);
    w.unmount();
  });
});

describe('changedValues', () => {
  it('lists only keys whose value differs, treating undefined and null as the same', () => {
    expect(changedValues(['a', 'b', 'c'], { a: 1, b: null, c: 'x' }, { a: 2, b: undefined, c: 'x' })).toEqual({ a: 2 });
    expect(changedValues(['a'], { a: [1] }, { a: [1] })).toEqual({});
  });
});
