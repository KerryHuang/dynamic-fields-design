import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import ShareDialog from './ShareDialog.vue';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { getReportShareTargets: vi.fn(), setReportShares: vi.fn() } }));
const api = vi.mocked(dynamicApi);
const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
let mounted: ReturnType<typeof mount> | undefined;

describe('ShareDialog', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    api.getReportShareTargets.mockResolvedValue([
      { id: 1, name: { 'zh-TW': '業務' } },
      { id: 2, name: { 'zh-TW': '營運' } },
    ]);
  });
  afterEach(() => { mounted?.unmount(); document.body.innerHTML = ''; });

  it('share dialog sends selected groups', async () => {
    api.setReportShares.mockResolvedValue(undefined as never);
    const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
    mounted = mount(ShareDialog, {
      props: { reportId: 9, open: true, groupIds: [1] },
      attachTo: document.body,
      global: { plugins: [[quasar.Quasar, { components: quasar, plugins: { Notify: quasar.Notify } }], i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
    });
    await flushPromises();
    q('share-group-2')!.click();
    await flushPromises();
    q('share-group-1')!.click();
    await flushPromises();
    q('share-save')!.click();
    await flushPromises();
    expect(api.setReportShares).toHaveBeenCalledWith(9, [2]);
  });

  it('load failure shows an error instead of an empty dialog, and cannot save', async () => {
    api.getReportShareTargets.mockRejectedValue(new Error('boom'));
    const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
    mounted = mount(ShareDialog, {
      props: { reportId: 9, open: true, groupIds: [1] },
      attachTo: document.body,
      global: { plugins: [[quasar.Quasar, { components: quasar, plugins: { Notify: quasar.Notify } }], i18n, [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]] },
    });
    await flushPromises();
    expect(q('share-load-error')).not.toBeNull();
    expect(q('share-group-1')).toBeNull();
    expect(document.body.textContent).not.toContain(zhTW.reports.noGroups);
    expect((q('share-save') as HTMLButtonElement).disabled).toBe(true);
  });
});
