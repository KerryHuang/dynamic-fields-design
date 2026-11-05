import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import type { BlockingRef, DeleteBlocked } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import DeleteBlockedDialog from './DeleteBlockedDialog.vue';

const ref = (i: number, title?: string | null): BlockingRef => ({
  moduleKey: 'orders', moduleName: { 'zh-TW': '訂單' }, fieldLabel: { 'zh-TW': '客戶' }, recordId: i, ...(title === undefined ? {} : { title }),
});

async function show(blocked: DeleteBlocked) {
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/:p(.*)*', component: { render: () => null } }] });
  await router.push('/');
  mount(DeleteBlockedDialog, {
    props: { modelValue: true, blocked },
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], router, createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } })] },
  });
  await flushPromises();
}
const items = () => Array.from(document.querySelectorAll('[data-testid="blocked-item"]'));

describe('DeleteBlockedDialog', () => {
  afterEach(() => { document.body.innerHTML = ''; });

  it('shows message, list and remaining count', async () => {
    await show({ message: '還有其他資料參照這筆記錄，不能刪除', references: Array.from({ length: 10 }, (_, i) => ref(i + 1, `單${i + 1}`)), total: 12 });
    expect(document.querySelector('[data-testid="blocked-message"]')!.textContent).toBe('還有其他資料參照這筆記錄，不能刪除');
    expect(items()).toHaveLength(10);
    expect(items()[0]!.textContent).toContain('訂單 · 客戶');
    expect(document.querySelector('[data-testid="blocked-remaining"]')!.textContent).toBe('還有 2 筆');
  });

  it('null title shows 無權限檢視', async () => {
    await show({ message: 'm', references: [ref(1, null), ref(2)], total: 2 });
    expect(items()[0]!.textContent).toContain(zhTW.dynamic.reference.restricted);
    expect(items()[1]!.textContent).toContain(zhTW.dynamic.reference.restricted);
    expect(document.querySelector('[data-testid="blocked-remaining"]')).toBeNull();
  });

  it('items without a title do not link', async () => {
    await show({ message: 'm', references: [ref(1, null), ref(2)], total: 2 });
    expect(items().every((i) => i.getAttribute('href') === null)).toBe(true);
  });

  it('item links to source record', async () => {
    await show({ message: 'm', references: [ref(9, '單九')], total: 1 });
    expect(items()[0]!.getAttribute('href')).toBe('/dynamic/orders/9');
  });
});
