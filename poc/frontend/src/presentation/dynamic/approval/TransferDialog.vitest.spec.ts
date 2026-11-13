import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { ApprovalInstanceView } from 'src/domain/dynamic/approval-types';
import zhTW from 'src/i18n/zh-TW.json';
import { useIdentityStore } from 'src/stores/use-identity-store';
import { bodyOf, HttpFail, mockHttp, type Call } from 'src/test-utils/mock-http';
import TransferDialog from './TransferDialog.vue';

const inst: ApprovalInstanceView = {
  id: 7, status: 'pending', routeKind: 'flow', flowName: null, fromOptionId: 1, toOptionId: 2, initiatorUserId: 'init', submittedTime: '', completedTime: null,
  rowVersion: 'iv1', actions: [], canWithdraw: false, canCancel: false, titleSnapshot: '',
  stages: [{
    id: 1, seq: 1, stageCode: 's', name: {}, mode: 'any', status: 'active', skipReason: null, dueTime: null, editableFieldKeys: [],
    tasks: [{ id: 11, assigneeUserId: 'me', assigneeName: '我', originalUserId: 'me', reason: 'resolved', status: 'open', actedTime: null, canAct: true, canReturnPrevious: false }],
  }],
};
const users = [{ userId: 'me', displayName: '我' }, { userId: 'init', displayName: '發起人' }, { userId: 'fin1', displayName: '財務一' }];

Element.prototype.scrollTo = () => {}; // jsdom has no scrollTo; the Quasar menu calls it

let calls: Call[];
let keys: unknown[];
let reply: unknown[];
const q = (id: string) => document.body.querySelector<HTMLElement>(`[data-testid="${id}"]`);
const settle = async () => { await flushPromises(); await new Promise((r) => setTimeout(r, 20)); await flushPromises(); };
const click = async (id: string) => { q(id)!.click(); await settle(); };
const posts = () => calls.filter((c) => c.method === 'post' && c.url.endsWith('/transfer'));

async function mountDialog() {
  setActivePinia(createPinia());
  useIdentityStore().setSession('me');
  keys = [];
  calls = mockHttp((c, config) => {
    if (c.method === 'post') {
      keys.push(config.headers?.['Idempotency-Key']);
      return reply.length > 1 ? reply.shift() : reply[0];
    }
    if (c.url === '/policies/effective') return { values: {} };
    if (c.url === '/users') return users;
    return {};
  });
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(TransferDialog, {
    props: { modelValue: true, recordId: 9, moduleKey: 'po', taskId: 11, instance: inst },
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
  });
  await settle();
  return w;
}

const wait = async () => { await new Promise((r) => setTimeout(r, 150)); await settle(); };
/** Types into the select the way a user does and returns what the menu lists. */
async function search(text: string) {
  const input = q('transfer-user')!.querySelector('input') as HTMLInputElement;
  input.focus();
  input.value = text;
  input.dispatchEvent(new Event('input'));
  await wait();
  return [...document.body.querySelectorAll<HTMLElement>('.q-item')];
}
async function pick(label: string) {
  (await search(label)).find((e) => e.textContent?.includes(label))!.click();
  await settle();
}
const type = async (v: string) => {
  const ta = q('transfer-comment') as HTMLTextAreaElement;   // the testid lands on the native textarea
  ta.value = v;
  ta.dispatchEvent(new Event('input'));
  await settle();
};

describe('TransferDialog', () => {
  beforeEach(() => {
    document.body.innerHTML = '';
    reply = [{ outcome: 'transferred', instanceId: 7, instanceRowVersion: 'iv2' }];
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });
  afterEach(() => { document.body.innerHTML = ''; });

  it('sends userId and comment with the instance rowVersion and one idempotency key per opening', async () => {
    const w = await mountDialog();
    await pick('財務一');
    await type('  出差中  ');
    await click('transfer-send');
    expect(posts()).toHaveLength(1);
    expect(posts()[0]!.url).toBe('/approval-tasks/11/transfer');
    expect(bodyOf(posts()[0]!)).toEqual({ rowVersion: 'iv1', userId: 'fin1', comment: '出差中' });
    expect(typeof keys[0]).toBe('string');
    expect(w.emitted('update:modelValue')?.at(-1)).toEqual([false]);
    w.unmount();
  });

  it('offers neither me nor the initiator', async () => {
    const w = await mountDialog();
    expect((await search('財務')).map((e) => e.textContent)).toEqual(['財務一']);
    expect((await search('發起')).map((e) => e.textContent)).toEqual(['沒有符合的選項']);
    expect((await search('我')).map((e) => e.textContent)).toEqual(['沒有符合的選項']);
    w.unmount();
  });

  it('does not send without a target', async () => {
    const w = await mountDialog();
    await click('transfer-send');
    expect(posts()).toHaveLength(0);
    expect(document.body.textContent).toContain('請選擇要轉交的對象');
    w.unmount();
  });

  it('shows a 400 on userId under the select', async () => {
    reply = [new HttpFail(400, { errors: { userId: ['不能交給發起人'] } })];
    const w = await mountDialog();
    await pick('財務一');
    await click('transfer-send');
    expect(q('transfer-user')!.textContent).toContain('不能交給發起人');
    expect(q('action-error')).toBeNull();
    w.unmount();
  });

  it('409 stale shows reload and keeps the dialog', async () => {
    reply = [new HttpFail(409, { code: 'stale', message: '簽核狀態已更新，請重新整理後再試' })];
    const w = await mountDialog();
    await pick('財務一');
    await click('transfer-send');
    expect(q('action-error')!.textContent).toContain(zhTW.dynamic.stale);
    expect(q('action-reload')).not.toBeNull();
    expect(q('transfer-send')).not.toBeNull();
    w.unmount();
  });
});
