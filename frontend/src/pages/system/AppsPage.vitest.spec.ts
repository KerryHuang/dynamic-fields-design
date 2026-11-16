import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi, type AppInfo, type AppPackageSummary, type OpenPreview } from 'src/services/api/dynamic-api';
import AppsPage from './AppsPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: {
    getMe: vi.fn(), getAppPackages: vi.fn(), getApps: vi.fn(), importAppPackage: vi.fn(),
    previewOpenApp: vi.fn(), openApp: vi.fn(), closeApp: vi.fn(), reopenApp: vi.fn(),
  },
}));
const api = vi.mocked(dynamicApi);

const pkg = (id: string, status: AppPackageSummary['status'], extra: Partial<AppPackageSummary> = {}): AppPackageSummary => ({
  id, version: '1.0.0', name: { 'zh-TW': `套件-${id}` }, source: 'imported', requires: { apps: [] }, groups: [], modules: [], status, ...extra,
});
const app = (key: string, status: AppInfo['status'], extra: Partial<AppInfo> = {}): AppInfo => ({
  key, name: { 'zh-TW': key }, packageVersion: '1.0.0', status, rowVersion: 'r', modules: [], dependents: [], unaffiliatedReferrers: [], warnings: [], ...extra,
});
const okPreview: OpenPreview = {
  errors: [], keyConflicts: [], hostEntityConflicts: [], missingDependencies: [],
  groups: [{ code: 'sales', name: { 'zh-TW': '業務' }, suggestion: 'map', suggestedGroupId: 7 }, { code: 'ops', name: { 'zh-TW': '營運' }, suggestion: 'create' }],
  warnings: [], canOpen: true,
};

let mounted: ReturnType<typeof mount> | undefined;
async function mountPage(packages: AppPackageSummary[], apps: AppInfo[], designer = { read: true, add: true, edit: true, delete: true }) {
  api.getMe.mockResolvedValue({ isSystemAdmin: true, designer } as never);
  api.getAppPackages.mockResolvedValue(packages);
  api.getApps.mockResolvedValue(apps);
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const Host = { render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(AppsPage))) };
  mounted = mount(Host, {
    attachTo: document.body,
    global: {
      plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], i18n, createPinia(),
        [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]],
    },
  });
  await flushPromises();
}
const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLButtonElement | null;
const all = (id: string) => [...document.body.querySelectorAll(`[data-testid="${id}"]`)] as HTMLElement[];
const click = async (id: string) => { q(id)!.click(); await flushPromises(); };

describe('AppsPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });
  afterEach(() => { mounted?.unmount(); document.body.innerHTML = ''; });

  it('shows one row per package with its state, and the action that fits the state', async () => {
    await mountPage([pkg('a', null), pkg('b', 'Open'), pkg('c', 'Closed')], [app('b', 'Open'), app('c', 'Closed')]);
    const rows = all('app-row');
    expect(rows).toHaveLength(3);
    expect(rows[0].querySelector('[data-testid="app-open"]')).not.toBeNull();
    expect(rows[1].querySelector('[data-testid="app-close"]')).not.toBeNull();
    expect(rows[2].querySelector('[data-testid="app-reopen"]')).not.toBeNull();
    expect(rows[2].textContent).toContain(zhTW.apps.status.Closed);
  });

  it('shows the empty hint with no packages', async () => {
    await mountPage([], []);
    expect(q('apps-empty')).not.toBeNull();
  });

  it('without designer add/edit there are no write actions', async () => {
    await mountPage([pkg('a', null), pkg('b', 'Open')], [app('b', 'Open')], { read: true, add: false, edit: false, delete: false });
    expect(q('app-open')).toBeNull();
    expect(q('app-close')).toBeNull();
    expect(q('apps-import')).toBeNull();
  });

  it('open: previews first, sends the suggested group mapping, and enables confirm only when canOpen', async () => {
    api.previewOpenApp.mockResolvedValue(okPreview);
    api.openApp.mockResolvedValue(app('a', 'Open'));
    await mountPage([pkg('a', null)], []);
    await click('app-open');
    expect(api.previewOpenApp).toHaveBeenNthCalledWith(1, 'a', { locale: 'zh-TW' });
    // 警告要看套用建議後的結果，所以第二次預覽帶著建議的對應
    expect(api.previewOpenApp).toHaveBeenNthCalledWith(2, 'a', {
      groupMap: { sales: { mode: 'map', groupId: 7 }, ops: { mode: 'create' } }, locale: 'zh-TW',
    });
    await click('open-confirm');
    expect(api.openApp).toHaveBeenCalledWith('a', {
      groupMap: { sales: { mode: 'map', groupId: 7 }, ops: { mode: 'create' } }, locale: 'zh-TW',
    });
  });

  it('open: masters opened along with it get their own group mapping (steward is not skipped)', async () => {
    api.previewOpenApp.mockResolvedValue({
      ...okPreview, groups: [],
      masters: [
        { id: 'bp', name: { 'zh-TW': '往來對象' }, action: 'open', groups: [{ code: 'steward', name: { 'zh-TW': '往來對象管理員' }, suggestion: 'create' }] },
        { id: 'ct', name: { 'zh-TW': '聯絡人' }, action: 'bind', groups: [] },
      ],
      unbound: [],
    });
    api.openApp.mockResolvedValue(app('a', 'Open'));
    await mountPage([pkg('a', null)], []);
    await click('app-open');
    const masters = { bp: { steward: { mode: 'create' } } };
    expect(api.previewOpenApp).toHaveBeenNthCalledWith(2, 'a', { groupMap: {}, masters, locale: 'zh-TW' });
    expect(q('open-masters')!.textContent).toContain('往來對象管理員');
    await click('open-confirm');
    expect(api.openApp).toHaveBeenCalledWith('a', { groupMap: {}, masters, locale: 'zh-TW' });
  });

  it('open: unbound master symbols are blockers', async () => {
    api.previewOpenApp.mockResolvedValue({ ...okPreview, canOpen: false, masters: [], unbound: ['ship.customer.fill.map: bp/partner.phone'] });
    await mountPage([pkg('a', null)], []);
    await click('app-open');
    expect(q('open-blockers')!.textContent).toContain('bp/partner.phone');
  });

  it('import: a merged master shows its warnings', async () => {
    api.importAppPackage.mockResolvedValue({ ...pkg('bp', 'Open'), merged: true, warnings: ['「電話」（phone）的型別和匯入的不同，保留既有的'] });
    await mountPage([], []);
    const notify = vi.spyOn(mounted!.findComponent(AppsPage).vm.$q, 'notify');
    const input = q('apps-file') as unknown as HTMLInputElement;
    const file = new File(['{}'], 'bp.app.json', { type: 'application/json' });
    Object.defineProperty(file, 'text', { value: () => Promise.resolve('{}') });
    Object.defineProperty(input, 'files', { value: [file] });
    input.dispatchEvent(new Event('change'));
    await flushPromises();
    expect(api.importAppPackage).toHaveBeenCalledWith('{}');
    expect(notify).toHaveBeenCalledWith(expect.objectContaining({ type: 'positive', message: zhTW.apps.merged }));
    expect(notify).toHaveBeenCalledWith(expect.objectContaining({ type: 'warning', message: '「電話」（phone）的型別和匯入的不同，保留既有的' }));
  });

  it('open: blockers are listed and confirm stays disabled (nothing is sent)', async () => {
    api.previewOpenApp.mockResolvedValue({
      ...okPreview, canOpen: false, keyConflicts: [{ moduleCode: 'cust', key: 'customer' }],
      missingDependencies: [{ kind: 'app', appId: 'crm', reason: 'notInstalled' }],
    });
    await mountPage([pkg('a', null)], []);
    await click('app-open');
    const blockers = q('open-blockers')!.textContent!;
    expect(blockers).toContain('customer');
    expect(blockers).toContain('crm');
    expect(q('open-confirm')!.disabled).toBe(true);
    await click('open-confirm');
    expect(api.openApp).not.toHaveBeenCalled();
  });

  it('close without dependents closes without cascade', async () => {
    api.closeApp.mockResolvedValue({ app: app('b', 'Closed'), closed: ['b'], warnings: [] });
    await mountPage([pkg('b', 'Open')], [app('b', 'Open')]);
    await click('app-close');
    expect(q('close-cascade')).toBeNull();
    await click('close-confirm');
    expect(api.closeApp).toHaveBeenCalledWith('b', false);
  });

  it('close with dependents names them and only offers cascade', async () => {
    api.closeApp.mockResolvedValue({ app: app('b', 'Closed'), closed: ['q', 'b'], warnings: [] });
    await mountPage([pkg('b', 'Open')], [app('b', 'Open', { dependents: ['quote'], unaffiliatedReferrers: ['my_mod'] })]);
    await click('app-close');
    expect(q('close-dependents')!.textContent).toContain('quote');
    expect(q('close-referrers')!.textContent).toContain('my_mod');
    expect(q('close-confirm')).toBeNull();
    await click('close-cascade');
    expect(api.closeApp).toHaveBeenCalledWith('b', true);
  });

  it('reopen calls the API with the app key', async () => {
    api.reopenApp.mockResolvedValue(app('c', 'Open'));
    await mountPage([pkg('c', 'Closed')], [app('c', 'Closed')]);
    await click('app-reopen');
    expect(api.reopenApp).toHaveBeenCalledWith('c');
  });

  it('shows the forbidden notice instead of the list when the API refuses a non-admin', async () => {
    const err = Object.assign(new Error('forbidden'), { status: 403 });
    api.getMe.mockResolvedValue({ isSystemAdmin: false, designer: { read: true, add: false, edit: false, delete: false } } as never);
    api.getAppPackages.mockRejectedValue(err);
    api.getApps.mockRejectedValue(err);
    const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
    const Host = { render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(AppsPage))) };
    mounted = mount(Host, {
      attachTo: document.body,
      global: {
        plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], i18n, createPinia(),
          [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]],
      },
    });
    await flushPromises();
    expect(q('forbidden')).not.toBeNull();
    expect(all('app-reopen')).toHaveLength(0);
  });
});
