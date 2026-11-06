import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import ReportListPage from './ReportListPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { getReports: vi.fn() } }));
const api = vi.mocked(dynamicApi);
const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
let mounted: ReturnType<typeof mount> | undefined;
let router: ReturnType<typeof createRouter>;

async function mountPage() {
  router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/reports', component: ReportListPage }, { path: '/reports/:id', component: { template: '<i/>' } }],
  });
  await router.push('/reports');
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  mounted = mount({ template: '<q-layout><q-page-container><router-view /></q-page-container></q-layout>' }, {
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar }], i18n, router, [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]] },
  });
  await flushPromises();
}

describe('ReportListPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });
  afterEach(() => { mounted?.unmount(); document.body.innerHTML = ''; });

  it('lists own and shared reports with owner, and opens one on click', async () => {
    api.getReports.mockResolvedValue([
      { id: 1, name: '我的月報', ownerName: '我', isOwner: true, sharedGroupIds: [2], mainModuleKey: 'orders' },
      { id: 2, name: '別人的報表', ownerName: '小王', isOwner: false, sharedGroupIds: [2], mainModuleKey: 'orders' },
    ]);
    await mountPage();
    expect(q('report-row-1')!.textContent).toContain('我的月報');
    expect(q('report-row-1')!.textContent).toContain('已分享');
    expect(q('report-row-2')!.textContent).toContain('小王');
    expect(q('report-row-2')!.textContent).not.toContain('已分享');
    q('report-row-2')!.click();
    await flushPromises();
    expect(router.currentRoute.value.path).toBe('/reports/2');
  });

  it('shows the empty text when there are no reports', async () => {
    api.getReports.mockResolvedValue([]);
    await mountPage();
    expect(document.body.textContent).toContain('還沒有報表');
  });

  it('403 shows the forbidden notice', async () => {
    api.getReports.mockRejectedValue({ status: 403 });
    await mountPage();
    expect(q('report-create')).toBeNull();
  });
});
