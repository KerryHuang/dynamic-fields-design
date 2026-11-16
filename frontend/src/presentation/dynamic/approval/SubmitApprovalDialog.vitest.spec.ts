import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import zhTW from 'src/i18n/zh-TW.json';
import { bodyOf, HttpFail, mockHttp, type Call } from 'src/test-utils/mock-http';
import SubmitApprovalDialog from './SubmitApprovalDialog.vue';

const user = (id: string, name: string) => ({ userId: id, displayName: name });
const flowPreview = (extra: Record<string, unknown> = {}) => ({
  outcome: 'flow', flowName: { 'zh-TW': '採購簽核' }, picks: [],
  stages: [
    { code: 's1', name: { 'zh-TW': '主管' }, status: 'enter', approvers: [user('u1', '王主管')], usesFallback: false },
    { code: 's2', name: { 'zh-TW': '財務' }, status: 'conditional', approvers: [], usesFallback: false },
  ],
  ...extra,
});
const pickReq = (min = 1, max = 2) => ({
  stageCode: 's3', name: { 'zh-TW': '複核' }, min, max, candidates: [user('c1', '甲'), user('c2', '乙'), user('c3', '丙')],
});

interface Opts { preview?: unknown; previews?: unknown[]; submit?: unknown[]; policy?: Record<string, unknown>; previewFail?: HttpFail }
let calls: Call[];
let keys: unknown[];
let qc: QueryClient;
const body = () => document.body;
const previews = () => calls.filter((c) => c.url === '/records/9/approval/preview');
const sends = () => calls.filter((c) => c.url === '/records/9/approval/submit');

async function mountDialog(o: Opts = {}) {
  keys = [];
  qc = new QueryClient();
  const queue = [...(o.submit ?? [{ outcome: 'submitted', instanceId: 1, recordRowVersion: 'v2' }])];
  const previewQueue = [...(o.previews ?? [])];
  calls = mockHttp((c, config) => {
    if (c.url === '/records/9/approval/preview') return o.previewFail ?? (previewQueue.length > 1 ? previewQueue.shift() : previewQueue[0]) ?? o.preview ?? flowPreview();
    if (c.url === '/records/9/approval/submit') { keys.push(config.headers?.['Idempotency-Key']); return queue.length > 1 ? queue.shift() : queue[0]; }
    if (c.url === '/policies/effective') return { values: o.policy ?? {} };
    return {};
  });
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(SubmitApprovalDialog, {
    props: { modelValue: true, recordId: 9, moduleKey: 'po', to: 12, toLabel: '已核准', recordRowVersion: 'v1' },
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], i18n, [VueQueryPlugin, { queryClient: qc }]] },
  });
  await flushPromises();
  await new Promise((r) => setTimeout(r, 20));
  await flushPromises();
  return w;
}
const sendBtn = () => body().querySelector<HTMLButtonElement>('[data-testid="submit-approval-send"]')!;
const click = async (el: Element) => { (el as HTMLElement).click(); await flushPromises(); await new Promise((r) => setTimeout(r, 20)); await flushPromises(); };

describe('SubmitApprovalDialog', () => {
  beforeEach(() => { document.body.innerHTML = ''; });
  afterEach(() => { document.body.innerHTML = ''; });

  it('renders flow name and stage approvers from preview', async () => {
    const w = await mountDialog();
    expect(body().querySelector('[data-testid="preview-flow"]')!.textContent).toContain('採購簽核');
    expect(body().querySelector('[data-testid="preview-stage-s1"]')!.textContent).toContain('王主管');
    expect(body().textContent).toContain(zhTW.approval.submit.orgNote);
    w.unmount();
  });

  it('conditional stage shows 依條件決定 and no skip wording', async () => {
    const w = await mountDialog();
    const stage = body().querySelector('[data-testid="preview-stage-s2"]')!.textContent!;
    expect(stage).toContain('依條件決定');
    expect(stage).not.toContain('跳過');
    w.unmount();
  });

  it('pick count follows preview min and max before submit is enabled', async () => {
    const w = await mountDialog({ preview: flowPreview({ picks: [pickReq(2, 2)], stages: [{ code: 's3', name: { 'zh-TW': '複核' }, status: 'pick', approvers: [], usesFallback: false }] }) });
    expect(body().querySelector('[data-testid="preview-stage-s3"]')!.textContent).toContain('由你選擇');
    expect(body().querySelector('[data-testid="pick-s3"]')!.textContent).toContain('選 2 位');
    expect(sendBtn().disabled).toBe(true);
    const select = w.findComponent({ name: 'QSelect' });
    select.vm.$emit('update:modelValue', ['c1']);
    await flushPromises();
    expect(sendBtn().disabled).toBe(true);
    select.vm.$emit('update:modelValue', ['c1', 'c2']);
    await flushPromises();
    expect(sendBtn().disabled).toBe(false);
    select.vm.$emit('update:modelValue', ['c1', 'c2', 'c3']);
    await flushPromises();
    expect(sendBtn().disabled).toBe(true);
    w.unmount();
  });

  it('direct outcome shows no-approval notice with the target state', async () => {
    const w = await mountDialog({ preview: { outcome: 'direct', flowName: null, stages: [], picks: [] } });
    expect(body().querySelector('[data-testid="preview-direct"]')!.textContent).toBe('這張單不需簽核，會直接轉為已核准');
    expect(sendBtn().disabled).toBe(false);
    w.unmount();
  });

  it('blocked outcome disables submit', async () => {
    const w = await mountDialog({ preview: { outcome: 'blocked', flowName: null, stages: [], picks: [] } });
    expect(body().querySelector('[data-testid="preview-blocked"]')!.textContent).toContain('沒有符合條件的簽核流程');
    expect(sendBtn().disabled).toBe(true);
    w.unmount();
  });

  it('submit body carries picks keyed by stage code and the record rowVersion', async () => {
    const w = await mountDialog({ preview: flowPreview({ picks: [pickReq(1, 2)] }) });
    w.findComponent({ name: 'QSelect' }).vm.$emit('update:modelValue', ['c2']);
    await flushPromises();
    (body().querySelector('textarea') as HTMLTextAreaElement).value = ' 請審 ';
    body().querySelector('textarea')!.dispatchEvent(new Event('input'));
    await flushPromises();
    await click(sendBtn());
    expect(bodyOf(sends()[0])).toEqual({ to: 12, recordRowVersion: 'v1', picks: { s3: ['c2'] }, comment: '請審' });
    expect(w.emitted('submitted')![0]).toEqual(['submitted']);
    expect(w.emitted('update:modelValue')!.at(-1)).toEqual([false]);
    w.unmount();
  });

  it('400 picks error shows under that pick select', async () => {
    const w = await mountDialog({
      preview: flowPreview({ picks: [pickReq(1, 2)] }),
      submit: [new HttpFail(400, { errors: { 'picks.s3': ['人數不符'], status: ['狀態錯誤'] } })],
    });
    w.findComponent({ name: 'QSelect' }).vm.$emit('update:modelValue', ['c1']);
    await flushPromises();
    await click(sendBtn());
    expect(body().querySelector('[data-testid="pick-s3"]')!.textContent).toContain('人數不符');
    expect(body().querySelector('[data-testid="submit-error"]')!.textContent).toContain('狀態錯誤');
    expect(previews()).toHaveLength(1);
    w.unmount();
  });

  it('400 for a pick stage not in the preview shows at the top and refetches the preview', async () => {
    const w = await mountDialog({ submit: [new HttpFail(400, { errors: { 'picks.s9': ['人數不符'] } })] });
    await click(sendBtn());
    expect(body().querySelector('[data-testid="submit-error"]')!.textContent).toContain('人數不符');
    expect(previews()).toHaveLength(2);
    w.unmount();
  });

  it('400 comment error shows under the comment box', async () => {
    const w = await mountDialog({ submit: [new HttpFail(400, { errors: { comment: ['意見太長'] } })] });
    await click(sendBtn());
    expect(body().textContent).toContain('意見太長');
    expect(body().querySelector('[data-testid="submit-error"]')).toBeNull();
    w.unmount();
  });

  it('409 stale keeps the dialog open, refetches record and preview and drops picks that are gone', async () => {
    const w = await mountDialog({
      previews: [flowPreview({ picks: [pickReq(1, 2)] }), flowPreview()],
      submit: [new HttpFail(409, { code: 'stale', message: 'x' }), { outcome: 'submitted', instanceId: 1, recordRowVersion: 'v2' }],
    });
    w.findComponent({ name: 'QSelect' }).vm.$emit('update:modelValue', ['c1']);
    await flushPromises();
    const spy = vi.spyOn(qc, 'invalidateQueries');
    await click(sendBtn());
    expect(w.emitted('update:modelValue')).toBeUndefined();
    expect(previews()).toHaveLength(2);
    expect(body().querySelector('[data-testid="submit-error"]')!.textContent).toBe(zhTW.dynamic.stale);
    expect(spy.mock.calls.map((c) => JSON.stringify((c[0] as { queryKey: unknown[] }).queryKey))).toContain('["record",9]');
    expect(body().querySelector('[data-testid="pick-s3"]')).toBeNull();
    await click(sendBtn());
    expect(bodyOf(sends()[1])).toEqual({ to: 12, recordRowVersion: 'v1' });
    w.unmount();
  });

  it('422 replaces the key for the next send', async () => {
    const w = await mountDialog({ submit: [new HttpFail(422, { message: '金鑰重複' }), { outcome: 'submitted', instanceId: 1, recordRowVersion: 'v2' }] });
    await click(sendBtn());
    await click(sendBtn());
    expect(keys).toHaveLength(2);
    expect(keys[1]).not.toBe(keys[0]);
    w.unmount();
  });

  it('preview load failure shows the error and keeps send disabled', async () => {
    const w = await mountDialog({ previewFail: new HttpFail(404, { message: '壞了' }) });
    expect(body().querySelector('[data-testid="preview-error"]')).not.toBeNull();
    expect(sendBtn().disabled).toBe(true);
    w.unmount();
  });

  it('retry after a network or 5xx error reuses the key; a success then replaces it', async () => {
    const w = await mountDialog({ submit: [new HttpFail(503), { outcome: 'submitted', instanceId: 1, recordRowVersion: 'v2' }] });
    await click(sendBtn());
    expect(body().querySelector('[data-testid="submit-error"]')).not.toBeNull();
    await click(sendBtn());
    expect(keys).toHaveLength(2);
    expect(keys[0]).toBeTruthy();
    expect(keys[1]).toBe(keys[0]);
    // The same mounted dialog sends again (the parent normally remounts it): the success must have issued a new key.
    await click(sendBtn());
    expect(keys).toHaveLength(3);
    expect(keys[2]).not.toBe(keys[1]);
    w.unmount();
  });

  it('direct outcome hides the comment box and sends no comment', async () => {
    const w = await mountDialog({ preview: { outcome: 'direct', flowName: null, stages: [], picks: [] } });
    expect(body().querySelector('textarea')).toBeNull();
    await click(sendBtn());
    expect(bodyOf(sends()[0])).toEqual({ to: 12, recordRowVersion: 'v1' });
    w.unmount();
  });

  it('is persistent so a stray click does not drop the input', async () => {
    const w = await mountDialog({ submit: [new HttpFail(503)] });
    expect(w.findComponent({ name: 'QDialog' }).props('persistent')).toBe(true);
    w.unmount();
  });

  it('comment counter uses policy max', async () => {
    const w = await mountDialog({ policy: { 'approval.maxCommentLength': 200 } });
    expect(body().querySelector('textarea')!.getAttribute('maxlength')).toBe('200');
    expect(body().textContent).toContain('0 / 200');
    w.unmount();
  });

  it('no policy value: no maxlength', async () => {
    const w = await mountDialog();
    expect(body().querySelector('textarea')!.getAttribute('maxlength')).toBeNull();
    w.unmount();
  });

  it('409 already pending closes the dialog and refetches', async () => {
    const w = await mountDialog();
    mockHttp((c) => {
      if (c.url === '/records/9/approval/preview') return flowPreview();
      if (c.url === '/records/9/approval/submit') return new HttpFail(409, { message: '這張單已在簽核中' });
      return { values: {} };
    });
    const spy = vi.spyOn(qc, 'invalidateQueries');
    await click(sendBtn());
    expect(spy.mock.calls.map((c) => JSON.stringify((c[0] as { queryKey: unknown[] }).queryKey))).toEqual(expect.arrayContaining(['["record",9]', '["record-approval",9]']));
    expect(w.emitted('update:modelValue')!.at(-1)).toEqual([false]);
    expect(w.emitted('submitted')).toBeUndefined();
    w.unmount();
  });
});
