import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import type { DynamicRecord, RelatedTab } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import RelatedTabs from './RelatedTabs.vue';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { related: vi.fn(), relatedPaged: vi.fn() } }));
const api = vi.mocked(dynamicApi);

const tab = (fieldId: number, canAdd: boolean): RelatedTab => ({
  fieldId, fieldKey: `f_${fieldId}`, fieldLabel: { 'zh-TW': `客戶${fieldId}` }, moduleKey: `src${fieldId}`, moduleName: { 'zh-TW': `訂單${fieldId}` }, canAdd,
});
const rec = (id: number): DynamicRecord => ({ id, moduleKey: 'src1', title: `單${id}`, values: {}, ownerUserId: 'a', createdUserId: 'a', createdTime: '', modifiedUserId: 'a', modifiedTime: '2026-01-02T03:04:00', rowVersion: 'v', references: {}, hostKey: null });
const paged = (items: DynamicRecord[], totalCount = items.length) => ({ items, totalCount, currentPage: 1, pagesSize: 10 });

async function mountTabs() {
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/:p(.*)*', component: { render: () => null } }] });
  await router.push('/');
  const w = mount(RelatedTabs, {
    props: { recordId: 7 },
    global: {
      plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], router,
        createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } }),
        [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]],
    },
  });
  await flushPromises();
  return w;
}

describe('RelatedTabs', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
    api.relatedPaged.mockResolvedValue(paged([rec(1)]));
  });

  it('renders one tab per related field with module and field label', async () => {
    api.related.mockResolvedValue([tab(1, true), tab(2, true)]);
    const w = await mountTabs();
    expect(w.find('[data-testid="related-tab-1"]').text()).toContain('訂單1 · 客戶1');
    expect(w.find('[data-testid="related-tab-2"]').text()).toContain('訂單2 · 客戶2');
  });

  it('tab lists source records with paging', async () => {
    api.related.mockResolvedValue([tab(1, true)]);
    api.relatedPaged.mockResolvedValue(paged([rec(1), rec(2)], 25));
    const w = await mountTabs();
    expect(api.relatedPaged).toHaveBeenCalledWith(7, 1, 1, 10);
    const rows = w.findAll('[data-testid="related-row"]');
    expect(rows).toHaveLength(2);
    expect(rows[0]!.text()).toContain('單1');
    expect(rows[0]!.attributes('href')).toBe('/dynamic/src1/1');
    await w.findAll('.q-pagination button').find((b) => b.text() === '2')!.trigger('click');
    await flushPromises();
    expect(api.relatedPaged).toHaveBeenCalledWith(7, 1, 2, 10);
  });

  it('row with hidden title shows the record id', async () => {
    api.related.mockResolvedValue([tab(1, true)]);
    api.relatedPaged.mockResolvedValue(paged([{ ...rec(3), title: '' }]));
    const w = await mountTabs();
    expect(w.find('[data-testid="related-row"]').text()).toContain('#3');
  });

  it('add button navigates with prefill when canAdd', async () => {
    api.related.mockResolvedValue([tab(1, true)]);
    const w = await mountTabs();
    expect(w.find('[data-testid="related-add"]').attributes('href')).toBe('/dynamic/src1/new?prefill=f_1:7');
  });

  it('no add button when canAdd false', async () => {
    api.related.mockResolvedValue([tab(1, false)]);
    const w = await mountTabs();
    expect(w.find('[data-testid="related-add"]').exists()).toBe(false);
  });

  it('renders nothing when no tabs', async () => {
    api.related.mockResolvedValue([]);
    const w = await mountTabs();
    expect(w.find('[data-testid="related-tabs"]').exists()).toBe(false);
  });
});
