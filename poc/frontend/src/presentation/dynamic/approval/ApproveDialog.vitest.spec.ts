import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { DynamicRecord, ModuleSchema, SchemaField } from 'src/domain/dynamic/types';
import type { ApprovalInstanceView } from 'src/domain/dynamic/approval-types';
import zhTW from 'src/i18n/zh-TW.json';
import { useIdentityStore } from 'src/stores/use-identity-store';
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

const inst: ApprovalInstanceView = {
  id: 7, status: 'pending', routeKind: 'flow', flowName: null, fromOptionId: 1, toOptionId: 2, initiatorUserId: 'init', submittedTime: '', completedTime: null,
  rowVersion: 'iv1', actions: [], canWithdraw: false, canCancel: false, titleSnapshot: '',
  stages: [{
    id: 1, seq: 1, stageCode: 's', name: {}, mode: 'all', status: 'active', skipReason: null, dueTime: null, editableFieldKeys: ['f_2'],
    tasks: [
      { id: 11, assigneeUserId: 'me', assigneeName: '我', originalUserId: 'me', reason: 'resolved', status: 'open', actedTime: null, canAct: true, canReturnPrevious: false },
      { id: 12, assigneeUserId: 'peer', assigneeName: '同僚', originalUserId: 'peer', reason: 'resolved', status: 'open', actedTime: null, canAct: true, canReturnPrevious: false },
    ],
  }],
};
const users = [
  { userId: 'me', displayName: '我' }, { userId: 'init', displayName: '發起人' }, { userId: 'peer', displayName: '同僚' },
  { userId: 'fin1', displayName: '財務一' }, { userId: 'fin2', displayName: '財務二' }, { userId: 'fin3', displayName: '財務三' },
];
Element.prototype.scrollTo = () => {}; // jsdom has no scrollTo; the Quasar menu calls it

let calls: Call[];
const body = () => document.body;
const q = (id: string) => body().querySelector<HTMLElement>(`[data-testid="${id}"]`);
const settle = async () => { await flushPromises(); await new Promise((r) => setTimeout(r, 20)); await flushPromises(); };
const inputOf = (label: string) => [...body().querySelectorAll<HTMLInputElement>('.q-field')].find((f) => f.textContent?.includes(label))!.querySelector('input')!;
const type = async (label: string, v: string) => { const i = inputOf(label); i.value = v; i.dispatchEvent(new Event('input')); await settle(); };
const posts = () => calls.filter((c) => c.method === 'post');

async function mountDialog(o: { keys?: string[]; reply?: unknown; schema?: ModuleSchema; record?: DynamicRecord; addSign?: boolean; values?: Record<string, unknown> } = {}) {
  setActivePinia(createPinia());
  useIdentityStore().setSession('me');
  calls = mockHttp((c) => {
    if (c.method === 'post') return o.reply ?? { outcome: 'advanced', instanceId: 7, instanceRowVersion: 'iv2' };
    if (c.url === '/policies/effective') return { values: o.values ?? {} };
    if (c.url === '/users') return users;
    return {};
  });
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(ApproveDialog, {
    attachTo: document.body,
    props: {
      modelValue: true, recordId: 9, moduleKey: 'po', taskId: 11, rowVersion: 'iv1', editableFieldKeys: o.keys ?? [], schema: o.schema ?? schema, record: o.record ?? record,
      ...(o.addSign && { addSign: true, instance: inst }),
    },
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
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

  it('sends the parsed value, as the normal form does: text is trimmed', async () => {
    const w = await mountDialog({ keys: ['f_2'] });
    await type('核定金額', '  new  ');
    q('approve-send')!.click();
    await settle();
    expect(bodyOf(posts()[0])).toEqual({ rowVersion: 'iv1', values: { f_2: 'new' } });
    w.unmount();
  });

  it('a reference field shows its title, not the id', async () => {
    const s = structuredClone(schema);
    s.blocks[0].fields[1] = { ...field(2, '供應商'), type: 'reference', reference: { kind: 'module', key: 'sup', name: { 'zh-TW': '供應商' }, lightUrl: '/modules/sup/records/light' } };
    const r = { ...record, values: { ...record.values, f_2: 1023 }, references: { f_2: { key: '1023', title: '虹光文具', state: 'ok' as const } } };
    const w = await mountDialog({ keys: ['f_2'], schema: s, record: r });
    expect(inputOf('供應商').value).toBe('虹光文具');
    w.unmount();
  });

  it('an error on a key that is not drawn still reaches the top', async () => {
    const w = await mountDialog({ keys: ['f_2', 'f_99'], reply: new HttpFail(400, { errors: { f_99: ['看不見的欄位'] } }) });
    await type('核定金額', 'new');
    q('approve-send')!.click();
    await settle();
    expect(q('action-error')!.textContent).toContain('看不見的欄位');
    w.unmount();
  });

  it('a stale 409 keeps the edited values for the resend', async () => {
    const w = await mountDialog({ keys: ['f_2'], reply: new HttpFail(409, { code: 'stale', message: 'stale' }) });
    await type('核定金額', 'new');
    q('approve-send')!.click();
    await settle();
    expect(q('action-error')!.textContent).toContain('重新載入');
    expect(inputOf('核定金額').value).toBe('new');
    await w.setProps({ rowVersion: 'iv2' });
    q('approve-send')!.click();
    await settle();
    expect(bodyOf(posts()[1])).toEqual({ rowVersion: 'iv2', values: { f_2: 'new' } });
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

/** Types into the add-sign user select the way a user does and returns the menu items. */
async function searchUsers(text: string) {
  const input = q('add-sign-users')!.querySelector('input') as HTMLInputElement;
  input.focus();
  input.value = text;
  input.dispatchEvent(new Event('input'));
  await new Promise((r) => setTimeout(r, 150));
  await settle();
  return [...body().querySelectorAll<HTMLElement>('.q-item')];
}
async function pickUser(label: string) {
  (await searchUsers(label)).find((e) => e.textContent?.includes(label))!.click();
  await settle();
}

describe('ApproveDialog add-sign', () => {
  beforeEach(() => { document.body.innerHTML = ''; vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} }); });
  afterEach(() => { document.body.innerHTML = ''; });

  it('add-sign mode requires at least one user', async () => {
    const w = await mountDialog({ addSign: true });
    expect(q('approve-dialog')!.textContent).toContain('同意並加簽');
    q('approve-send')!.click();
    await settle();
    expect(posts()).toHaveLength(0);
    expect(body().textContent).toContain('請選擇至少一位加簽對象');
    w.unmount();
  });

  it('caps the users at the policy value', async () => {
    const w = await mountDialog({ addSign: true, values: { 'approval.maxAddSignUsers': 2 } });
    expect(q('add-sign-cap')!.textContent).toContain('最多 2 位');
    await pickUser('財務一');
    await pickUser('財務二');
    const items = await searchUsers('財務');
    const third = items.find((e) => e.textContent?.includes('財務三'))!;
    expect(third.getAttribute('aria-disabled') === 'true' || third.classList.contains('disabled')).toBe(true);
    w.unmount();
  });

  it('offers seat holders of the current stage, but not me or the initiator', async () => {
    const w = await mountDialog({ addSign: true });
    expect((await searchUsers('同僚')).map((e) => e.textContent)).toEqual(['同僚']);   // a new stage: holding a vote here does not exclude
    expect((await searchUsers('發起')).map((e) => e.textContent)).toEqual(['沒有符合的選項']);
    expect((await searchUsers('我')).map((e) => e.textContent)).toEqual(['沒有符合的選項']);
    w.unmount();
  });

  it('sends userIds, mode and only changed values', async () => {
    const w = await mountDialog({ addSign: true, keys: ['f_2'] });
    await pickUser('財務一');
    await pickUser('財務二');
    await type('核定金額', 'new');
    expect(q('add-sign-mode')!.textContent).toContain('或簽');
    (q('add-sign-mode')!.querySelectorAll('.q-radio')[1] as HTMLElement).click();   // 會簽
    await settle();
    q('approve-send')!.click();
    await settle();
    expect(posts()).toHaveLength(1);
    expect(posts()[0]!.url).toBe('/approval-tasks/11/add-sign');
    expect(bodyOf(posts()[0]!)).toEqual({ rowVersion: 'iv1', userIds: ['fin1', 'fin2'], mode: 'all', values: { f_2: 'new' } });
    w.unmount();
  });

  it('defaults to any and shows a 400 on userIds under the select', async () => {
    const w = await mountDialog({ addSign: true, reply: new HttpFail(400, { errors: { userIds: ['這位使用者在這一關已經有簽核任務'] } }) });
    await pickUser('財務一');
    q('approve-send')!.click();
    await settle();
    expect(bodyOf(posts()[0]!)).toMatchObject({ mode: 'any' });
    expect(q('add-sign-users')!.textContent).toContain('這位使用者在這一關已經有簽核任務');
    w.unmount();
  });

  it('approve mode unchanged: no add-sign fields and the approve endpoint', async () => {
    const w = await mountDialog();
    expect(q('add-sign-users')).toBeNull();
    expect(q('approve-send')!.textContent).toContain('同意');
    q('approve-send')!.click();
    await settle();
    expect(posts()[0]!.url).toBe('/approval-tasks/11/approve');
    expect(bodyOf(posts()[0]!)).toEqual({ rowVersion: 'iv1' });
    w.unmount();
  });
});
