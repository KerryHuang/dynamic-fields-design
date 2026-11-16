import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import zhTW from 'src/i18n/zh-TW.json';
import { bodyOf, HttpFail, mockHttp, type Call } from 'src/test-utils/mock-http';
import CancelApprovalDialog from './CancelApprovalDialog.vue';

let calls: Call[];
let reply: unknown;
let qc: QueryClient;
const q = (id: string) => document.body.querySelector<HTMLElement>(`[data-testid="${id}"]`);
const settle = async () => { await flushPromises(); await new Promise((r) => setTimeout(r, 20)); await flushPromises(); };
const click = async (id: string) => { q(id)!.click(); await settle(); };
const posts = () => calls.filter((c) => c.method === 'post' && c.url.endsWith('/cancel'));
const type = async (v: string) => {
  const ta = q('cancel-comment') as HTMLTextAreaElement;
  ta.value = v;
  ta.dispatchEvent(new Event('input'));
  await settle();
};

async function mountDialog() {
  setActivePinia(createPinia());
  qc = new QueryClient();
  calls = mockHttp((c) => {
    if (c.method === 'post') return reply;
    if (c.url === '/policies/effective') return { values: {} };
    return {};
  });
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(CancelApprovalDialog, {
    props: { modelValue: true, recordId: 9, moduleKey: 'po', instanceId: 7, rowVersion: 'iv1' },
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], i18n, [VueQueryPlugin, { queryClient: qc }]] },
  });
  await settle();
  return w;
}

describe('CancelApprovalDialog', () => {
  beforeEach(() => {
    document.body.innerHTML = '';
    reply = { outcome: 'cancelled', instanceId: 7, instanceRowVersion: 'iv2' };
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });
  afterEach(() => { document.body.innerHTML = ''; });

  it('Cancel requires a comment before sending', async () => {
    const w = await mountDialog();
    await click('cancel-send');
    expect(posts()).toHaveLength(0);
    expect(document.body.textContent).toContain(zhTW.approval.actions.commentRequiredCancel);
    await type('   ');
    await click('cancel-send');
    expect(posts()).toHaveLength(0);
    w.unmount();
  });

  it('sends rowVersion and comment', async () => {
    const w = await mountDialog();
    await type('  供應商報價已過期  ');
    await click('cancel-send');
    expect(posts()).toHaveLength(1);
    expect(posts()[0]!.url).toBe('/approval-instances/7/cancel');
    expect(bodyOf(posts()[0]!)).toEqual({ rowVersion: 'iv1', comment: '供應商報價已過期' });
    expect(w.emitted('update:modelValue')?.at(-1)).toEqual([false]);
    w.unmount();
  });

  it('409 shows the server message and refreshes', async () => {
    reply = new HttpFail(409, { message: '這一關已由他人處理' });
    const w = await mountDialog();
    const notify = vi.spyOn(w.vm.$q as { notify: (o: unknown) => void }, 'notify').mockImplementation(() => {});
    const spy = vi.spyOn(qc, 'invalidateQueries');
    await type('原因');
    await click('cancel-send');
    expect(notify.mock.calls.map((c) => (c[0] as { message: string }).message)).toContain('這一關已由他人處理');
    expect(spy).toHaveBeenCalledWith({ queryKey: ['record-approval', 9] });
    expect(w.emitted('update:modelValue')?.at(-1)).toEqual([false]);
    w.unmount();
  });
});
