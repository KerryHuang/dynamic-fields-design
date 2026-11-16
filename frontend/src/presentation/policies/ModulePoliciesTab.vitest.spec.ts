import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { ModulePolicy } from 'src/domain/dynamic/policy-types';
import type { ModuleDetail } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { FACTORY_ME_THEME } from 'src/domain/theme/theme-fixtures';
import { useIdentityStore } from 'src/stores/use-identity-store';
import { bodyOf, mockHttp, type Call } from 'src/test-utils/mock-http';
import ModulePoliciesTab from './ModulePoliciesTab.vue';

const CODE = 'task.maxRulesPerModule';
const mod = (): ModuleDetail => ({ id: 1, key: 'm1', programCode: 'DYN_M1', name: { 'zh-TW': '模組' }, status: 'Active', version: 1, blocks: [] });
const policy = (o: Partial<ModulePolicy> = {}): ModulePolicy => ({
  code: CODE, category: 'task', kind: 'integer', unit: 'count', min: 1, max: 100, systemValue: 20, effective: 20, ...o,
});

let calls: Call[];
let w: VueWrapper;
async function mountTab(opts: { canEdit?: boolean; policies?: ModulePolicy[] } = {}) {
  const pinia = createPinia();
  setActivePinia(pinia);
  useIdentityStore().setSession('admin');
  const { canEdit = true, policies = [policy()] } = opts;
  calls = mockHttp((c) => {
    if (c.url === '/me') return { userId: 'admin', displayName: 'A', isSystemAdmin: false, mustChangePassword: false, groups: [], designer: { read: true, add: true, edit: canEdit, delete: false }, theme: FACTORY_ME_THEME };
    if (c.url === '/users' || c.url === '/groups') return [];
    if (c.url === '/modules/m1/policies' && c.method === 'get') return policies;
    return policy();
  });
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  w = mount(ModulePoliciesTab, {
    props: { module: mod() },
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], pinia, i18n, [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]] },
  });
  await flushPromises();
}
const q = <T extends Element>(id: string) => document.body.querySelector<T>(`[data-testid="${id}"]`);
const radioChecked = (id: string) => q(id)?.getAttribute('aria-checked');
async function typeValue(value: string) {
  const el = q('policy-row-' + CODE)!.querySelector('input[type="number"]') as HTMLInputElement;
  el.value = value;
  el.dispatchEvent(new Event('input'));
  await flushPromises();
}

describe('ModulePoliciesTab', () => {
  beforeEach(() => {
    document.body.innerHTML = '';
    (globalThis as { ResizeObserver?: unknown }).ResizeObserver = class { observe() {} unobserve() {} disconnect() {} };
  });

  it('沒有覆寫時選沿用全系統並顯示全系統值', async () => {
    await mountTab();
    expect(radioChecked(`policy-inherit-${CODE}`)).toBe('true');
    expect(radioChecked(`policy-override-${CODE}`)).toBe('false');
    expect(q('policy-row-' + CODE)!.textContent).toContain('沿用全系統（20）');
    expect((q(`policy-save-${CODE}`) as HTMLButtonElement).disabled).toBe(true);
  });

  it('選覆寫填 5 存檔送 PUT body { value: 5 }', async () => {
    await mountTab();
    q<HTMLElement>(`policy-override-${CODE}`)!.click();
    await flushPromises();
    await typeValue('5');
    q<HTMLElement>(`policy-save-${CODE}`)!.click();
    await flushPromises();
    const put = calls.find((c) => c.method === 'put')!;
    expect(put.url).toBe('/modules/m1/policies/task.maxRulesPerModule');
    expect(bodyOf(put)).toEqual({ value: 5 });
  });

  it('覆寫值改成 5 後清空輸入框，儲存鈕停用', async () => {
    await mountTab();
    q<HTMLElement>(`policy-override-${CODE}`)!.click();
    await flushPromises();
    await typeValue('5');
    expect((q(`policy-save-${CODE}`) as HTMLButtonElement).disabled).toBe(false);
    await typeValue('');
    expect((q(`policy-save-${CODE}`) as HTMLButtonElement).disabled).toBe(true);
  });

  it('override 為 null 視同沿用全系統', async () => {
    await mountTab({ policies: [policy({ override: null as never })] });
    expect(radioChecked(`policy-inherit-${CODE}`)).toBe('true');
    expect((q(`policy-save-${CODE}`) as HTMLButtonElement).disabled).toBe(true);
  });

  it('沿用全系統送 null', async () => {
    await mountTab({ policies: [policy({ override: 5, effective: 5 })] });
    expect(radioChecked(`policy-override-${CODE}`)).toBe('true');
    q<HTMLElement>(`policy-inherit-${CODE}`)!.click();
    await flushPromises();
    q<HTMLElement>(`policy-save-${CODE}`)!.click();
    await flushPromises();
    expect(bodyOf(calls.find((c) => c.method === 'put'))).toEqual({ value: null });
  });

  it('非設計器編輯者唯讀', async () => {
    await mountTab({ canEdit: false });
    expect(q(`policy-save-${CODE}`)).toBeNull();
    expect(q(`policy-inherit-${CODE}`)!.getAttribute('aria-disabled')).toBe('true');
  });
});
