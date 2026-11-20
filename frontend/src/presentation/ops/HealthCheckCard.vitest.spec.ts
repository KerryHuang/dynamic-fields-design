import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { HealthCheckConfig } from 'src/domain/ops/ops-types';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import { bodyOf, HttpFail, mockHttp, type Call } from 'src/test-utils/mock-http';
import HealthCheckCard from './HealthCheckCard.vue';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
let calls: Call[];
let w: ReturnType<typeof mount> | undefined;

const check = (o: Partial<HealthCheckConfig> = {}): HealthCheckConfig => ({
  code: 'storage.diskFree', category: 'storage', runner: 'watchdog', unit: 'basisPoints', direction: 'lowerIsWorse', applicable: true, canDisable: true,
  enabled: { value: true, factory: true, isFactory: true },
  intervalMinutes: { value: 5, factory: 5, isFactory: true, min: 1, max: 60 },
  warn: { value: 1500, factory: 1500, isFactory: true, min: 100, max: 5000 },
  critical: { value: 500, factory: 500, isFactory: true, min: 50, max: 5000 },
  state: { code: 'storage.diskFree', category: 'storage', runner: 'watchdog', scope: 'instance', status: 'ok', value: 4200, unit: 'basisPoints', fromMemory: false },
  ...o,
});

async function make(c: HealthCheckConfig, resultsFail?: HttpFail) {
  calls = mockHttp((call) => (call.method === 'put' ? c : resultsFail ?? { items: [] }));
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  w = mount(HealthCheckCard, {
    attachTo: document.body,
    props: { check: c },
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], i18n, [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]] },
  });
  await flushPromises();
}
const q = (id: string) => document.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
afterEach(() => { w?.unmount(); document.body.innerHTML = ''; });

async function openSettings() {
  (q('health-expand-storage.diskFree')!.querySelector('.q-item') as HTMLElement).click();
  await flushPromises();
  q('health-settings-tab-storage.diskFree')!.click();
  await flushPromises();
}

describe('HealthCheckCard', () => {
  it('狀態與萬分比數值照語系顯示', async () => {
    await make(check());
    expect(q('health-status-storage.diskFree')!.textContent).toContain('正常：42%');
  });

  it('設定顯示 API 給的出廠值與允許範圍；全部留空儲存＝送 null 回到出廠值', async () => {
    await make(check());
    await openSettings();
    expect(q('health-warn-storage.diskFree')!.closest('.q-field')!.textContent).toContain('出廠 15%，允許 1% 到 50%');
    q('health-save-storage.diskFree')!.click();
    await flushPromises();
    expect(bodyOf(calls.find((c) => c.method === 'put'))).toEqual({ intervalMinutes: null, enabled: null, warn: null, critical: null });
  });

  it('改過的值帶入欄位並原樣送出', async () => {
    await make(check({ warn: { value: 2000, factory: 1500, isFactory: false, min: 100, max: 5000 } }));
    await openSettings();
    const warn = q('health-warn-storage.diskFree')!;
    expect(((warn.tagName === 'INPUT' ? warn : warn.querySelector('input')) as HTMLInputElement).value).toBe('2000');
    q('health-save-storage.diskFree')!.click();
    await flushPromises();
    expect(bodyOf(calls.find((c) => c.method === 'put'))).toMatchObject({ warn: 2000 });
  });

  it('審查修正 3：趨勢查詢失敗顯示載入失敗，不當成「沒有資料」', async () => {
    await make(check(), new HttpFail(500, { message: 'boom' }));
    (q('health-expand-storage.diskFree')!.querySelector('.q-item') as HTMLElement).click();
    await flushPromises();
    expect(q('health-trend-failed-storage.diskFree')).not.toBeNull();
    expect(q('health-expand-storage.diskFree')!.textContent).not.toContain('這段期間沒有資料');
  });

  it('審查修正 5：明確設成和出廠相同的「啟用」，只改間隔時照樣送 true（不清成跟隨出廠）', async () => {
    await make(check({ enabled: { value: true, factory: true, isFactory: false } }));
    await openSettings();
    const iv = q('health-interval-storage.diskFree')!;
    const el = (iv.tagName === 'INPUT' ? iv : iv.querySelector('input')) as HTMLInputElement;
    el.value = '10';
    el.dispatchEvent(new Event('input'));
    await flushPromises();
    q('health-save-storage.diskFree')!.click();
    await flushPromises();
    expect(bodyOf(calls.find((c) => c.method === 'put'))).toMatchObject({ intervalMinutes: 10, enabled: true });
    expect(q('health-enabled-source-storage.diskFree')!.textContent).toContain('自訂');
  });

  it('審查修正 5：切換開關後送明確值；按「改回出廠值」才送 null', async () => {
    await make(check());
    await openSettings();
    expect(q('health-enabled-source-storage.diskFree')!.textContent).toContain('跟隨出廠值');
    expect(q('health-follow-factory-storage.diskFree')).toBeNull();
    q('health-enabled-storage.diskFree')!.click();
    await flushPromises();
    q('health-enabled-storage.diskFree')!.click();
    await flushPromises();
    q('health-save-storage.diskFree')!.click();
    await flushPromises();
    expect(bodyOf(calls.filter((c) => c.method === 'put').at(-1))).toMatchObject({ enabled: true });

    q('health-follow-factory-storage.diskFree')!.click();
    await flushPromises();
    expect(q('health-enabled-source-storage.diskFree')!.textContent).toContain('跟隨出廠值');
    q('health-save-storage.diskFree')!.click();
    await flushPromises();
    expect(bodyOf(calls.filter((c) => c.method === 'put').at(-1))).toMatchObject({ enabled: null });
  });

  it('Demo 站的磁碟檢查顯示「不適用（Demo 站）」，沒有立即檢查與設定', async () => {
    await make(check({
      applicable: false, enabled: { value: false, factory: false, isFactory: true },
      state: { code: 'storage.diskFree', category: 'storage', runner: 'watchdog', scope: 'instance', status: 'notApplicable', messageCode: 'demoSite', unit: 'basisPoints', fromMemory: false },
    }));
    expect(q('health-status-storage.diskFree')!.textContent).toContain('不適用（Demo 站）');
    expect(q('health-run-storage.diskFree')).toBeNull();
    expect(q('health-expand-storage.diskFree')).toBeNull();
  });
});
