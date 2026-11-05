import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { RecordHistoryEntry } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import RecordHistory from './RecordHistory.vue';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { recordHistory: vi.fn() } }));
const api = vi.mocked(dynamicApi);

const entry = (p: Partial<RecordHistoryEntry>): RecordHistoryEntry => ({
  id: 1, operation: 'update', fieldKey: 'f_1', fieldLabel: { 'zh-TW': '狀態' }, oldText: '草稿', newText: '已送出',
  changedUserId: 'u1', changedUserName: '王小明', changedTime: '2026-03-04T05:06:00', ...p,
});
const paged = (items: RecordHistoryEntry[], totalCount = items.length) => ({ items, totalCount, currentPage: 1, pagesSize: 20 });

async function mountHistory() {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW, 'en-US': zhTW } });
  const w = mount(RecordHistory, {
    props: { recordId: 7 },
    global: {
      plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], i18n,
        [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]],
    },
  });
  await flushPromises();
  return { w, i18n };
}

describe('RecordHistory', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  it('lists entries newest first with field label, old → new, user and time', async () => {
    api.recordHistory.mockResolvedValue(paged([
      entry({ id: 2, changedTime: '2026-03-04T05:06:00' }),
      entry({ id: 1, fieldLabel: { 'zh-TW': '備註' }, oldText: 'a', newText: 'b', changedTime: '2026-03-01T00:00:00' }),
    ]));
    const { w } = await mountHistory();
    expect(api.recordHistory).toHaveBeenCalledWith(7, 1, 20, 'zh-TW');
    const rows = w.findAll('tbody tr');
    expect(rows).toHaveLength(2);
    const t = rows[0]!.text();
    expect(t).toContain('2026-03-04 05:06');
    expect(t).toContain('王小明');
    expect(t).toContain('狀態');
    expect(t).toContain('草稿');
    expect(t).toContain('已送出');
    expect(rows[1]!.text()).toContain('備註');
    expect(w.text()).toContain('歷程');
  });

  it('shows create and delete as sentences, not as field rows', async () => {
    api.recordHistory.mockResolvedValue(paged([
      entry({ id: 2, operation: 'delete', fieldKey: undefined, fieldLabel: undefined, oldText: undefined, newText: undefined }),
      entry({ id: 1, operation: 'create', fieldKey: undefined, fieldLabel: undefined, oldText: undefined, newText: undefined }),
    ]));
    const { w } = await mountHistory();
    const rows = w.findAll('tbody tr');
    expect(rows[0]!.text()).toContain('刪除了這筆記錄');
    expect(rows[1]!.text()).toContain('建立了這筆記錄');
  });

  it('prefixes detail-row entries with the detail module and row title', async () => {
    const detail = { moduleKey: 'line', moduleName: { 'zh-TW': '明細' }, recordId: 11, title: '品名 A' };
    api.recordHistory.mockResolvedValue(paged([
      entry({ id: 2, fieldLabel: { 'zh-TW': '數量' }, detail }),
      entry({ id: 1, fieldLabel: { 'zh-TW': '數量' }, detail: { ...detail, title: '' } }),
    ]));
    const { w } = await mountHistory();
    const rows = w.findAll('tbody tr');
    expect(rows[0]!.text()).toContain('明細 › 品名 A：數量');
    expect(rows[1]!.text()).toContain('明細：數量');
    expect(rows[1]!.text()).not.toContain('›');
  });

  it('prefixes detail-row create and delete with the detail module and row title', async () => {
    const detail = { moduleKey: 'line', moduleName: { 'zh-TW': '明細' }, recordId: 11, title: '品名 A' };
    const none = { fieldKey: undefined, fieldLabel: undefined, oldText: undefined, newText: undefined };
    api.recordHistory.mockResolvedValue(paged([
      entry({ id: 2, operation: 'delete', ...none, detail }),
      entry({ id: 1, operation: 'create', ...none, detail }),
    ]));
    const { w } = await mountHistory();
    const rows = w.findAll('tbody tr');
    expect(rows[0]!.text()).toContain('明細 › 品名 A：刪除了這筆記錄');
    expect(rows[1]!.text()).toContain('明細 › 品名 A：建立了這筆記錄');
  });

  it('shows empty state', async () => {
    api.recordHistory.mockResolvedValue(paged([]));
    const { w } = await mountHistory();
    expect(w.text()).toContain('尚無歷程');
  });

  it('pages with q-pagination when total exceeds the page size', async () => {
    api.recordHistory.mockResolvedValue(paged([entry({})], 45));
    const { w } = await mountHistory();
    await w.findAll('.q-pagination button').find((b) => b.text() === '2')!.trigger('click');
    await flushPromises();
    expect(api.recordHistory).toHaveBeenCalledWith(7, 2, 20, 'zh-TW');
  });

  it('goes back to page 1 when the record changes', async () => {
    api.recordHistory.mockResolvedValue(paged([entry({})], 45));
    const { w } = await mountHistory();
    await w.findAll('.q-pagination button').find((b) => b.text() === '2')!.trigger('click');
    await flushPromises();
    await w.setProps({ recordId: 8 });
    await flushPromises();
    expect(api.recordHistory).toHaveBeenLastCalledWith(8, 1, 20, 'zh-TW');
    expect(api.recordHistory).not.toHaveBeenCalledWith(8, 2, 20, 'zh-TW');
  });

  it('no pagination when everything fits one page', async () => {
    api.recordHistory.mockResolvedValue(paged([entry({})], 5));
    const { w } = await mountHistory();
    expect(w.find('.q-pagination').exists()).toBe(false);
  });

  it('refetches when the locale changes', async () => {
    api.recordHistory.mockResolvedValue(paged([entry({})]));
    const { i18n } = await mountHistory();
    i18n.global.locale.value = 'en-US';
    await flushPromises();
    expect(api.recordHistory).toHaveBeenLastCalledWith(7, 1, 20, 'en-US');
  });
});
