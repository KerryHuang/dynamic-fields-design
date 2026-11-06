import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { DeleteCheck, DeleteTargetRef } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import type { ApiError } from 'src/services/api/http';
import DeleteDialog from './DeleteDialog.vue';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { deleteCheck: vi.fn(), deleteTarget: vi.fn() } }));
const api = vi.mocked(dynamicApi);

const clean: DeleteCheck = { canDelete: true, blockers: [], purge: { records: 3, indexRows: 9, historyRows: 8 } };
const blocked: DeleteCheck = {
  canDelete: false, purge: { records: 0, indexRows: 0, historyRows: 0 },
  blockers: [
    { kind: 'referenceField', moduleKey: 'order', id: 12, name: '客戶' },
    { kind: 'liveRecords', moduleKey: 'cust', id: 1, name: '客戶', count: 7 },
  ],
};

async function mountDialog(target: DeleteTargetRef, name = '客戶') {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(DeleteDialog, {
    props: { modelValue: true, target, name }, attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, plugins: { Notify: quasar.Notify } }], i18n] },
  });
  await flushPromises();
  return w;
}
const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
const submit = () => q('delete-submit') as HTMLButtonElement;
const typeKey = async (value: string) => {
  // Quasar 把 data-testid 放在原生 input 上
  const input = q('delete-confirm-input') as HTMLInputElement;
  input.value = value;
  input.dispatchEvent(new Event('input'));
  await flushPromises();
};

describe('DeleteDialog', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    document.body.innerHTML = '';
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  it('checks first, and lists every blocker with its source when deletion is blocked; no submit button', async () => {
    api.deleteCheck.mockResolvedValue(blocked);
    const w = await mountDialog({ kind: 'module', key: 'cust' });
    expect(api.deleteCheck).toHaveBeenCalledWith({ kind: 'module', key: 'cust' });
    const list = q('delete-blockers')!.textContent!;
    expect(list).toContain('參照欄位');
    expect(list).toContain('order');
    expect(list).toContain('客戶');
    expect(list).toContain('7');
    expect(q('delete-submit')).toBeNull();
    w.unmount();
  });

  it('module: warns with the purge counts and enables the button only after typing the module key', async () => {
    api.deleteCheck.mockResolvedValue(clean);
    const w = await mountDialog({ kind: 'module', key: 'cust' });
    const warn = q('delete-warning')!.textContent!;
    expect(warn).toContain('永久');
    expect(warn).toContain('3');
    expect(warn).toContain('9');
    expect(warn).toContain('8');
    expect(submit().disabled).toBe(true);
    await typeKey('cus');
    expect(submit().disabled).toBe(true);
    await typeKey('cust');
    expect(submit().disabled).toBe(false);
    w.unmount();
  });

  it('confirming sends the purge count the user saw, then emits deleted', async () => {
    api.deleteCheck.mockResolvedValue(clean);
    api.deleteTarget.mockResolvedValue(undefined as never);
    const w = await mountDialog({ kind: 'module', key: 'cust' });
    await typeKey('cust');
    submit().click();
    await flushPromises();
    expect(api.deleteTarget).toHaveBeenCalledWith({ kind: 'module', key: 'cust' }, 3);
    expect(w.emitted('deleted')).toHaveLength(1);
    expect(w.emitted('update:modelValue')?.at(-1)).toEqual([false]);
    w.unmount();
  });

  it('field and block need a checkbox instead of typing a key', async () => {
    api.deleteCheck.mockResolvedValue({ ...clean, purge: { records: 0, indexRows: 0, historyRows: 0 } });
    const w = await mountDialog({ kind: 'field', id: 5 }, '備註');
    expect(api.deleteCheck).toHaveBeenCalledWith({ kind: 'field', id: 5 });
    expect(q('delete-confirm-input')).toBeNull();
    expect(submit().disabled).toBe(true);
    (q('delete-confirm-check') as HTMLElement).click();
    await flushPromises();
    expect(submit().disabled).toBe(false);
    w.unmount();
  });

  it('a stale purge count (409 with a fresh check) shows the new numbers and asks to confirm again', async () => {
    api.deleteCheck.mockResolvedValue(clean);
    const fresh: DeleteCheck = { ...clean, purge: { records: 5, indexRows: 15, historyRows: 12 } };
    api.deleteTarget.mockRejectedValue({ status: 409, message: '要清除的資料筆數已改變', deleteCheck: fresh } satisfies ApiError);
    const w = await mountDialog({ kind: 'module', key: 'cust' });
    await typeKey('cust');
    submit().click();
    await flushPromises();
    expect(q('delete-warning')!.textContent).toContain('5');
    expect(q('delete-stale')).not.toBeNull();
    expect(w.emitted('deleted')).toBeUndefined();
    expect(submit().disabled).toBe(true); // 要重新輸入代碼確認
    w.unmount();
  });

  it('a 409 that turns into blockers (someone referenced it meanwhile) switches to the blocker list', async () => {
    api.deleteCheck.mockResolvedValue(clean);
    api.deleteTarget.mockRejectedValue({ status: 409, message: '不符合刪除條件', deleteCheck: blocked } satisfies ApiError);
    const w = await mountDialog({ kind: 'module', key: 'cust' });
    await typeKey('cust');
    submit().click();
    await flushPromises();
    expect(q('delete-blockers')).not.toBeNull();
    expect(q('delete-submit')).toBeNull();
    w.unmount();
  });

  it('403 on the check shows a no-permission message and no submit button', async () => {
    api.deleteCheck.mockRejectedValue({ status: 403 } satisfies ApiError);
    const w = await mountDialog({ kind: 'module', key: 'cust' });
    expect(q('delete-forbidden')).not.toBeNull();
    expect(q('delete-submit')).toBeNull();
    w.unmount();
  });
});
