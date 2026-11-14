import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { ApprovalInstanceView, ApprovalTaskView } from 'src/domain/dynamic/approval-types';
import zhTW from 'src/i18n/zh-TW.json';
import { bodyOf, mockHttp, type Call } from 'src/test-utils/mock-http';
import ReassignDialog from './ReassignDialog.vue';

const task = (id: number, user: string, name: string, p: Partial<ApprovalTaskView> = {}): ApprovalTaskView => ({
  id, assigneeUserId: user, assigneeName: name, originalUserId: user, reason: 'resolved', status: 'open', actedTime: null, canAct: false, canReturnPrevious: false, ...p,
});
const inst = (tasks: ApprovalTaskView[]): ApprovalInstanceView => ({
  id: 7, status: 'pending', routeKind: 'flow', flowName: null, fromOptionId: 1, toOptionId: 2, initiatorUserId: 'init', submittedTime: '', completedTime: null,
  rowVersion: 'iv1', actions: [], canWithdraw: false, canCancel: true, titleSnapshot: '',
  stages: [{ id: 1, seq: 1, stageCode: 's', name: {}, mode: 'all', status: 'active', skipReason: null, dueTime: null, editableFieldKeys: [], tasks }],
});
const users = [{ userId: 'admin', displayName: '系統管理員' }, { userId: 'init', displayName: '發起人' }, { userId: 'gm', displayName: '進銷存主管' },
  { userId: 'mgr', displayName: '業務主管' }, { userId: 'fin1', displayName: '財務一' }];

Element.prototype.scrollTo = () => {};

let calls: Call[];
const q = (id: string) => document.body.querySelector<HTMLElement>(`[data-testid="${id}"]`);
const settle = async () => { await flushPromises(); await new Promise((r) => setTimeout(r, 20)); await flushPromises(); };
const wait = async () => { await new Promise((r) => setTimeout(r, 150)); await settle(); };
const click = async (id: string) => { q(id)!.click(); await settle(); };
const posts = () => calls.filter((c) => c.method === 'post' && c.url.endsWith('/reassign'));

async function mountDialog(instance: ApprovalInstanceView) {
  setActivePinia(createPinia());
  calls = mockHttp((c) => {
    if (c.method === 'post') return { outcome: 'reassigned', instanceId: 7, instanceRowVersion: 'iv2' };
    if (c.url === '/policies/effective') return { values: {} };
    if (c.url === '/users') return users;
    return {};
  });
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(ReassignDialog, {
    props: { modelValue: true, recordId: 9, moduleKey: 'po', instance },
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
  });
  await settle();
  return w;
}

async function search(testid: string, text: string) {
  const input = q(testid)!.querySelector('input') as HTMLInputElement;
  input.focus();
  input.value = text;
  input.dispatchEvent(new Event('input'));
  await wait();
  return [...document.body.querySelectorAll<HTMLElement>('.q-item')];
}
async function pick(testid: string, label: string) {
  (await search(testid, label)).find((e) => e.textContent?.includes(label))!.click();
  await settle();
}

describe('ReassignDialog', () => {
  beforeEach(() => {
    document.body.innerHTML = '';
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });
  afterEach(() => { document.body.innerHTML = ''; });

  it('Reassign preselects the only open task', async () => {
    const w = await mountDialog(inst([task(11, 'gm', '進銷存主管')]));
    expect((q('reassign-task')!.querySelector('input') as HTMLInputElement).value).toBe('進銷存主管');
    await pick('reassign-user', '業務主管');
    await click('reassign-send');
    expect(posts()).toHaveLength(1);
    expect(posts()[0]!.url).toBe('/approval-tasks/11/reassign');
    expect(bodyOf(posts()[0]!)).toEqual({ rowVersion: 'iv1', userId: 'mgr' });
    w.unmount();
  });

  it('lists every open task of the current stage', async () => {
    const w = await mountDialog(inst([task(11, 'gm', '進銷存主管'), task(12, 'fin1', '財務一'), task(13, 'x', '已同意者', { status: 'approved' })]));
    expect((q('reassign-task')!.querySelector('input') as HTMLInputElement).value).toBe('');
    const items = (await search('reassign-task', '')).map((e) => e.textContent);
    expect(items).toEqual(['進銷存主管', '財務一']);
    w.unmount();
  });

  it('sends the chosen task id', async () => {
    const w = await mountDialog(inst([task(11, 'gm', '進銷存主管'), task(12, 'fin1', '財務一')]));
    await click('reassign-send');
    expect(posts()).toHaveLength(0);   // no task and no target chosen yet
    expect(document.body.textContent).toContain(zhTW.approval.actions.reassignTaskRequired);
    await pick('reassign-task', '財務一');
    await pick('reassign-user', '業務主管');
    await click('reassign-send');
    expect(posts()[0]!.url).toBe('/approval-tasks/12/reassign');
    expect(bodyOf(posts()[0]!)).toEqual({ rowVersion: 'iv1', userId: 'mgr' });
    w.unmount();
  });
});
