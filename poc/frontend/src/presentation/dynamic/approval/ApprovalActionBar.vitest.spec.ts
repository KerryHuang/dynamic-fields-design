import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import type { ApprovalInstanceView, ApprovalStageView, ApprovalTaskView } from 'src/domain/dynamic/approval-types';
import zhTW from 'src/i18n/zh-TW.json';
import { bodyOf, HttpFail, mockHttp, type Call } from 'src/test-utils/mock-http';
import { useIdentityStore } from 'src/stores/use-identity-store';
import { approvalTaskActions } from './approval-actions';
import ApprovalActionBar from './ApprovalActionBar.vue';

const task = (p: Partial<ApprovalTaskView> = {}): ApprovalTaskView => ({
  id: 11, assigneeUserId: 'me', assigneeName: '我', originalUserId: 'me', reason: 'resolved', status: 'open', actedTime: null, canAct: true, ...p,
});
const stage = (p: Partial<ApprovalStageView> = {}): ApprovalStageView => ({
  id: 1, seq: 1, stageCode: 's', name: { 'zh-TW': '主管' }, mode: 'all', status: 'active', skipReason: null, dueTime: null, tasks: [task()], editableFieldKeys: [], ...p,
});
const inst = (p: Partial<ApprovalInstanceView> = {}): ApprovalInstanceView => ({
  id: 7, status: 'pending', routeKind: 'flow', flowName: null, fromOptionId: 1, toOptionId: 2, initiatorUserId: 'a', submittedTime: '', completedTime: null,
  rowVersion: 'iv1', stages: [stage()], actions: [], canWithdraw: false, canCancel: false, titleSnapshot: '', ...p,
});
const twoStage = () => inst({ stages: [stage({ id: 1, seq: 1, status: 'approved', tasks: [] }), stage({ id: 2, seq: 2 })] });

let calls: Call[];
let keys: Record<string, unknown[]>;
let qc: QueryClient;
let replies: Record<string, unknown[]>;
const body = () => document.body;
const q = (id: string) => body().querySelector<HTMLElement>(`[data-testid="${id}"]`);
const settle = async () => { await flushPromises(); await new Promise((r) => setTimeout(r, 20)); await flushPromises(); };
const click = async (id: string) => { q(id)!.click(); await settle(); };
const notices = (w: { vm: { $q: { notify: unknown } } }) => vi.spyOn(w.vm.$q as { notify: (o: unknown) => void }, 'notify').mockImplementation(() => {});
const said = (n: ReturnType<typeof notices>) => n.mock.calls.map((c) => (c[0] as { message: string }).message).join('|');
const posts = (suffix: string) => calls.filter((c) => c.method === 'post' && c.url.endsWith(suffix));

async function mountBar(instance: ApprovalInstanceView, o: { user?: string; width?: number; reply?: Record<string, unknown[]> } = {}) {
  setActivePinia(createPinia());
  useIdentityStore().setSession(o.user ?? 'me');
  keys = {};
  replies = o.reply ?? {};
  qc = new QueryClient();
  calls = mockHttp((c, config) => {
    if (c.method === 'post') {
      keys[c.url] = [...(keys[c.url] ?? []), config.headers?.['Idempotency-Key']];
      const queue = replies[c.url];
      if (queue) { const r = queue.length > 1 ? queue.shift() : queue[0]; return r; }
      return { outcome: 'advanced', instanceId: 7, instanceRowVersion: 'iv2' };
    }
    if (c.url === '/policies/effective') return { values: {} };
    return {};
  });
  window.innerWidth = o.width ?? 1366;
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const Host = { render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(quasar.QPage, null, () => h(ApprovalActionBar, { instance, recordId: 9, moduleKey: 'po' })))) };
  const w = mount(Host, {
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], i18n, [VueQueryPlugin, { queryClient: qc }]] },
  });
  window.dispatchEvent(new Event('resize'));
  await new Promise((r) => setTimeout(r, 150));
  await settle();
  return w;
}

describe('ApprovalActionBar', () => {
  beforeEach(() => {
    document.body.innerHTML = '';
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });
  afterEach(() => { document.body.innerHTML = ''; approvalTaskActions.length = 0; window.innerWidth = 1024; });

  it('approver sees approve and reject; others see none', async () => {
    const w = await mountBar(inst());
    expect(q('action-approve')).not.toBeNull();
    expect(q('action-reject')).not.toBeNull();
    expect(q('action-withdraw')).toBeNull();
    w.unmount();
    document.body.innerHTML = '';
    const other = await mountBar(inst({ stages: [stage({ tasks: [task({ canAct: false })] })] }), { user: 'x' });
    expect(q('action-approve')).toBeNull();
    expect(q('action-reject')).toBeNull();
    other.unmount();
  });

  it('initiator sees withdraw; disabled with reason when the current stage has acted', async () => {
    const w = await mountBar(inst({ stages: [stage({ tasks: [task({ canAct: false })] })], canWithdraw: true }), { user: 'a' });
    expect((q('action-withdraw') as HTMLButtonElement).disabled).toBe(false);
    w.unmount();
    document.body.innerHTML = '';
    const blocked = await mountBar(inst({ stages: [stage({ tasks: [task({ canAct: false })] })], canWithdraw: false }), { user: 'a' });
    expect((q('action-withdraw') as HTMLButtonElement).disabled).toBe(true);
    expect(q('withdraw-blocked')!.textContent).toContain('目前關卡已有人處理，不能撤回');
    blocked.unmount();
  });

  it('reject requires a comment before sending', async () => {
    const w = await mountBar(inst());
    await click('action-reject');
    await click('reject-send');
    expect(posts('/reject')).toHaveLength(0);
    expect(body().textContent).toContain('請填寫退回意見');
    w.unmount();
  });

  it('return to previous disabled when no earlier approved stage', async () => {
    const w = await mountBar(inst());
    await click('action-reject');
    expect(q('reject-to-previous')!.getAttribute('aria-disabled')).toBe('true');
    expect(body().textContent).toContain('前面沒有已同意的關卡');
    w.unmount();
    document.body.innerHTML = '';
    const two = await mountBar(twoStage());
    await click('action-reject');
    expect(q('reject-to-previous')!.getAttribute('aria-disabled')).not.toBe('true');
    two.unmount();
  });

  it('reject sends target, comment and instance rowVersion', async () => {
    const w = await mountBar(twoStage(), { reply: { '/approval-tasks/11/reject': [{ outcome: 'returned', instanceId: 7, instanceRowVersion: 'iv2' }] } });
    await click('action-reject');
    (q('reject-to-previous')!.querySelector('input') as HTMLInputElement | null)?.click();
    q('reject-to-previous')!.click();
    const ta = q('reject-comment') as HTMLTextAreaElement;
    ta.value = '金額不符';
    ta.dispatchEvent(new Event('input'));
    await settle();
    await click('reject-send');
    expect(bodyOf(posts('/reject')[0])).toEqual({ rowVersion: 'iv1', comment: '金額不符', to: 'previous' });
    w.unmount();
  });

  it('approve sends the task id, instance rowVersion and an Idempotency-Key', async () => {
    const w = await mountBar(inst());
    await click('action-approve');
    await click('approve-send');
    const sent = posts('/approval-tasks/11/approve');
    expect(sent).toHaveLength(1);
    expect(bodyOf(sent[0])).toEqual({ rowVersion: 'iv1' });
    expect(typeof keys['/approval-tasks/11/approve']![0]).toBe('string');
    w.unmount();
  });

  it('approve with a comment sends it trimmed', async () => {
    const w = await mountBar(inst());
    await click('action-approve');
    const ta = q('approve-comment') as HTMLTextAreaElement;
    ta.value = '  同意  ';
    ta.dispatchEvent(new Event('input'));
    await settle();
    await click('approve-send');
    expect(bodyOf(posts('/approve')[0])).toEqual({ rowVersion: 'iv1', comment: '同意' });
    w.unmount();
  });

  it('retry after network error reuses the key; success replaces it', async () => {
    const w = await mountBar(inst(), { reply: { '/approval-tasks/11/approve': [new HttpFail(500, {}), { outcome: 'advanced', instanceId: 7, instanceRowVersion: 'iv2' }] } });
    await click('action-approve');
    await click('approve-send');
    expect(q('action-error')).not.toBeNull();
    await click('approve-send');
    const k = keys['/approval-tasks/11/approve']!;
    expect(k).toHaveLength(2);
    expect(k[1]).toBe(k[0]);
    expect(q('approve-send')).toBeNull();           // success closed the dialog
    await click('action-approve');
    await click('approve-send');
    expect(keys['/approval-tasks/11/approve']![2]).not.toBe(k[0]);
    w.unmount();
  });

  it('rerouted outcome shows not-counted message with the new flow name and refetches', async () => {
    const w = await mountBar(inst(), { reply: { '/approval-tasks/11/approve': [{ outcome: 'rerouted', instanceId: 8, instanceRowVersion: 'n', newFlowName: { 'zh-TW': '大額流程' } }] } });
    const spy = vi.spyOn(qc, 'invalidateQueries');
    const n = notices(w);
    await click('action-approve');
    await click('approve-send');
    expect(said(n)).toContain('流程改為「大額流程」重新開始，你的同意未計入');
    expect(spy).toHaveBeenCalledWith({ queryKey: ['record-approval', 9] });
    w.unmount();
  });

  it('rerouted without flow name shows the fallback wording', async () => {
    const w = await mountBar(inst(), { reply: { '/approval-tasks/11/approve': [{ outcome: 'rerouted', instanceId: 8, instanceRowVersion: 'n', newFlowName: null }] } });
    const n = notices(w);
    await click('action-approve');
    await click('approve-send');
    expect(said(n)).toContain('改由備援簽核人重新簽核，你的同意未計入');
    w.unmount();
  });

  it('409 state changed offers reload and refetches', async () => {
    const w = await mountBar(inst(), { reply: { '/approval-tasks/11/approve': [new HttpFail(409, { code: 'stale', message: 'x' })] } });
    const spy = vi.spyOn(qc, 'invalidateQueries');
    await click('action-approve');
    await click('approve-send');
    expect(q('action-error')!.textContent).toContain(zhTW.dynamic.stale);
    expect(q('action-reload')).not.toBeNull();
    expect(spy).toHaveBeenCalledWith({ queryKey: ['record-approval', 9] });
    expect(q('approve-send')).not.toBeNull();       // dialog stays so the user can resend
    w.unmount();
  });

  it('409 stage handled shows server message and refetches', async () => {
    const w = await mountBar(inst(), { reply: { '/approval-tasks/11/approve': [new HttpFail(409, { message: '這一關已由他人處理' })] } });
    const spy = vi.spyOn(qc, 'invalidateQueries');
    const n = notices(w);
    await click('action-approve');
    await click('approve-send');
    expect(said(n)).toContain('這一關已由他人處理');
    expect(spy).toHaveBeenCalledWith({ queryKey: ['record-approval', 9] });
    expect(q('approve-send')).toBeNull();
    w.unmount();
  });

  it('withdraw sends the instance id and rowVersion', async () => {
    const w = await mountBar(inst({ stages: [stage({ tasks: [task({ canAct: false })] })], canWithdraw: true }), { user: 'a' });
    await click('action-withdraw');
    await click('withdraw-send');
    const sent = posts('/approval-instances/7/withdraw');
    expect(bodyOf(sent[0])).toEqual({ rowVersion: 'iv1' });
    w.unmount();
  });

  it('mobile width shows a bottom bar with approve and reject only', async () => {
    const w = await mountBar(inst(), { width: 390 });
    expect(q('approval-mobile-bar')).not.toBeNull();
    expect(q('action-approve')).not.toBeNull();
    expect(q('action-reject')).not.toBeNull();
    expect(q('approval-more')).toBeNull();
    w.unmount();
  });

  it('desktop width has no bottom bar', async () => {
    const w = await mountBar(inst());
    expect(q('approval-mobile-bar')).toBeNull();
    w.unmount();
  });

  it('extra actions appear in the more menu', async () => {
    approvalTaskActions.push({ key: 'transfer', label: () => '轉交', visible: () => true, run: () => {} });
    const w = await mountBar(inst(), { width: 390 });
    expect(q('approval-more')).not.toBeNull();
    w.unmount();
  });
});
