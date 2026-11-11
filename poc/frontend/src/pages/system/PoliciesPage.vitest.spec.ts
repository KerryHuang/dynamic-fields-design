import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import type { SystemPolicy } from 'src/domain/dynamic/policy-types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import PoliciesPage from './PoliciesPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: { getMe: vi.fn(), getSystemPolicies: vi.fn(), saveSystemPolicy: vi.fn() },
}));
const api = vi.mocked(dynamicApi);

const RULES: SystemPolicy = {
  code: 'task.maxRulesPerModule', category: 'task', kind: 'integer', scope: 'module', unit: 'count', min: 1, max: 100, factory: 20, effective: 20,
};
const PACKAGE: SystemPolicy = {
  code: 'task.maxRulesPerPackage', category: 'task', kind: 'integer', scope: 'system', unit: 'count', min: 1, max: 500, factory: 50, effective: 50,
};
let mounted: ReturnType<typeof mount> | undefined;

async function mountPage(over: { isSystemAdmin?: boolean; policies?: SystemPolicy[] } = {}) {
  api.getMe.mockResolvedValue({
    userId: 'admin', displayName: 'A', mustChangePassword: false, isSystemAdmin: over.isSystemAdmin ?? true, groups: [],
    designer: { read: true, add: true, edit: true, delete: true },
  } as never);
  api.getSystemPolicies.mockResolvedValue(over.policies ?? [RULES, PACKAGE]);
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const Host = { render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(PoliciesPage))) };
  mounted = mount(Host, {
    attachTo: document.body,
    global: {
      plugins: [[quasar.Quasar, { components: quasar, plugins: { Notify: quasar.Notify } }], i18n, createPinia(),
        [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]],
    },
  });
  await flushPromises();
}
const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
async function type(row: string, value: string) {
  const input = q(row)!.querySelector('input') as HTMLInputElement;
  input.value = value;
  input.dispatchEvent(new Event('input'));
  await flushPromises();
  return input;
}

describe('PoliciesPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });
  afterEach(() => { mounted?.unmount(); document.body.innerHTML = ''; });

  it('非系統管理員看到禁止頁、不呼叫 getSystemPolicies', async () => {
    await mountPage({ isSystemAdmin: false });
    expect(q('forbidden')).toBeTruthy();
    expect(api.getSystemPolicies).not.toHaveBeenCalled();
  });

  it('依分類分組並顯示出廠值與範圍', async () => {
    await mountPage();
    const row = q('policy-row-task.maxRulesPerModule')!;
    expect(row.textContent).toContain('每個模組的任務規則數上限');
    expect(row.textContent).toContain('20');
    expect(row.textContent).toContain('1 到 100');
    expect(row.textContent).toContain('可模組覆寫');
    expect(q('policy-row-task.maxRulesPerPackage')!.textContent).not.toContain('可模組覆寫');
    expect(document.body.textContent).toContain('任務');
  });

  it('改值後按儲存送出 number', async () => {
    await mountPage();
    expect((q('policy-save-task.maxRulesPerModule') as HTMLButtonElement).disabled).toBe(true);
    await type('policy-row-task.maxRulesPerModule', '30');
    api.saveSystemPolicy.mockResolvedValue({ ...RULES, value: 30, effective: 30 });
    q('policy-save-task.maxRulesPerModule')!.click();
    await flushPromises();
    expect(api.saveSystemPolicy).toHaveBeenCalledWith('task.maxRulesPerModule', 30);
  });

  it('沒改過時不顯示恢復出廠值；改過的按下送 null', async () => {
    await mountPage({ policies: [RULES, { ...PACKAGE, value: 30, effective: 30 }] });
    expect(q('policy-reset-task.maxRulesPerModule')).toBeNull();
    api.saveSystemPolicy.mockResolvedValue({ ...PACKAGE });
    q('policy-reset-task.maxRulesPerPackage')!.click();
    await flushPromises();
    expect(api.saveSystemPolicy).toHaveBeenCalledWith('task.maxRulesPerPackage', null);
  });

  it('儲存失敗顯示伺服器訊息、保留草稿', async () => {
    await mountPage();
    const input = await type('policy-row-task.maxRulesPerModule', '0');
    api.saveSystemPolicy.mockRejectedValue({ status: 400, errors: { value: ['必須是 1 到 100 的整數'] } });
    q('policy-save-task.maxRulesPerModule')!.click();
    await flushPromises();
    expect(q('policy-error-task.maxRulesPerModule')!.textContent).toContain('必須是 1 到 100 的整數');
    expect(input.value).toBe('0');
  });
});
