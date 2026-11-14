import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { ApprovalDelegate } from 'src/domain/dynamic/approval-types';
import zhTW from 'src/i18n/zh-TW.json';
import { useIdentityStore } from 'src/stores/use-identity-store';
import { bodyOf, HttpFail, mockHttp, type Call } from 'src/test-utils/mock-http';
import DelegateSettingsDialog from './DelegateSettingsDialog.vue';

const users = [{ userId: 'me', displayName: '我' }, { userId: 'sales01', displayName: '業務一' }, { userId: 'fin1', displayName: '財務一' }];
const saved: ApprovalDelegate = { delegateUserId: 'sales01', delegateName: '業務一', startDate: '2026-11-17', endDate: '2026-11-21', active: false };

Element.prototype.scrollTo = () => {}; // jsdom has no scrollTo; the Quasar menu calls it

let calls: Call[];
let current: ApprovalDelegate | '';
let putReply: unknown;
const q = (id: string) => document.body.querySelector<HTMLElement>(`[data-testid="${id}"]`);
const settle = async () => { await flushPromises(); await new Promise((r) => setTimeout(r, 20)); await flushPromises(); };
const click = async (id: string) => { q(id)!.click(); await settle(); };
const puts = () => calls.filter((c) => c.method === 'put' && c.url === '/me/approval-delegate');

async function mountDialog() {
  setActivePinia(createPinia());
  useIdentityStore().setSession('me');
  calls = mockHttp((c) => {
    if (c.url === '/me/approval-delegate') {
      if (c.method === 'put') return putReply;
      if (c.method === 'delete') { current = ''; return ''; }
      return current;
    }
    if (c.url === '/users') return users;
    return {};
  });
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(DelegateSettingsDialog, {
    props: { modelValue: true },
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
  });
  await settle();
  return w;
}

const wait = async () => { await new Promise((r) => setTimeout(r, 150)); await settle(); };
async function search(text: string) {
  const input = q('delegate-user')!.querySelector('input') as HTMLInputElement;
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
async function setDate(id: string, v: string) {
  const input = q(id) as HTMLInputElement;   // the testid lands on the native input
  input.value = v;
  input.dispatchEvent(new Event('input'));
  await settle();
}

describe('DelegateSettingsDialog', () => {
  beforeEach(() => {
    document.body.innerHTML = '';
    current = '';
    putReply = saved;
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });
  afterEach(() => { document.body.innerHTML = ''; });

  it('dialog excludes me from the delegate options', async () => {
    const w = await mountDialog();
    expect((await search('我')).map((e) => e.textContent)).toEqual(['沒有符合的選項']);
    expect((await search('業務')).map((e) => e.textContent)).toEqual(['業務一']);
    w.unmount();
  });

  it('saves the delegate and dates', async () => {
    const w = await mountDialog();
    expect(q('delegate-clear')).toBeNull();   // nothing to clear yet
    await pick('業務一');
    await setDate('delegate-start', '2026-11-17');
    await setDate('delegate-end', '2026-11-21');
    await click('delegate-save');
    expect(puts()).toHaveLength(1);
    expect(bodyOf(puts()[0])).toEqual({ delegateUserId: 'sales01', startDate: '2026-11-17', endDate: '2026-11-21' });
    expect(w.emitted('update:modelValue')?.at(-1)).toEqual([false]);
    w.unmount();
  });

  it('blocks save when start is after end', async () => {
    const w = await mountDialog();
    await pick('業務一');
    await setDate('delegate-start', '2026-11-22');
    await setDate('delegate-end', '2026-11-21');
    await click('delegate-save');
    expect(puts()).toHaveLength(0);
    expect(q('delegate-dialog')!.textContent).toContain('起日不能晚於迄日');
    w.unmount();
  });

  it('shows server 400 under the right field', async () => {
    putReply = new HttpFail(400, { errors: { endDate: ['迄日不能早於今天'], other: ['別的問題'] } });
    const w = await mountDialog();
    await pick('業務一');
    await click('delegate-save');
    const end = q('delegate-end')!.closest('.q-field') as HTMLElement;
    expect(end.textContent).toContain('迄日不能早於今天');
    expect(q('delegate-error')!.textContent).toContain('別的問題');
    expect(w.emitted('update:modelValue')).toBeUndefined();
    w.unmount();
  });

  it('prefills the saved setting; clear removes the setting', async () => {
    current = saved;
    const w = await mountDialog();
    expect((q('delegate-start') as HTMLInputElement).value).toBe('2026-11-17');
    expect((q('delegate-end') as HTMLInputElement).value).toBe('2026-11-21');
    await click('delegate-save');   // unchanged values go back as they were
    expect(bodyOf(puts()[0])).toEqual({ delegateUserId: 'sales01', startDate: '2026-11-17', endDate: '2026-11-21' });
    await click('delegate-clear');
    expect(calls.filter((c) => c.method === 'delete' && c.url === '/me/approval-delegate')).toHaveLength(1);
    expect(w.emitted('update:modelValue')?.at(-1)).toEqual([false]);
    w.unmount();
  });
});
