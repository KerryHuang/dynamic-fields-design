import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { nextTick } from 'vue';
import { createI18n } from 'vue-i18n';
import type { ModuleSchema } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import ExportMenu from './ExportMenu.vue';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { exportRecords: vi.fn() } }));
const api = vi.mocked(dynamicApi);

const schema = (printExport: boolean) => ({
  key: 'm', permissions: { read: true, add: true, edit: true, delete: true, printExport, pricing: true },
}) as unknown as ModuleSchema;
const query = { filter: '{"field":2,"op":"eq","value":"x"}', sort: [{ field: 2, order: 'desc' as const }], q: 'kw' };

function mountMenu(printExport = true, locale = 'zh-TW') {
  const i18n = createI18n({ legacy: false, locale, messages: { 'zh-TW': zhTW, th: zhTW } });
  const w = mount(ExportMenu, {
    props: { schema: schema(printExport), query },
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], i18n] },
    attachTo: document.body,
  });
  mounted.push(w);
  return w;
}
async function pick(w: ReturnType<typeof mountMenu>, id: string) {
  await w.get('[data-testid="export-btn"]').trigger('click');
  await nextTick();
  await new Promise((r) => setTimeout(r, 50));
  (document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement).click();
  await flushPromises();
}

const mounted: { unmount: () => void }[] = [];

describe('ExportMenu', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    // keep body: Quasar's Notify container lives there once created
    for (const w of mounted.splice(0)) w.unmount();
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  it('is hidden without printExport permission', () => {
    expect(mountMenu(true).find('[data-testid="export-btn"]').exists()).toBe(true);
    expect(mountMenu(false).find('[data-testid="export-btn"]').exists()).toBe(false);
  });

  it('passes the current filter, sort, keyword and locale and saves the blob with the server file name', async () => {
    const blob = new Blob(['x']);
    api.exportRecords.mockResolvedValue({ blob, fileName: 'm-20261104-1200.xlsx' });
    const create = vi.fn(() => 'blob:u');
    const revoke = vi.fn();
    vi.stubGlobal('URL', Object.assign(URL, { createObjectURL: create, revokeObjectURL: revoke }));
    const clicked: { download: string; href: string }[] = [];
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
      clicked.push({ download: this.download, href: this.href });
    });
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      const w = mountMenu(true, 'th');
      await pick(w, 'export-xlsx');
      expect(api.exportRecords).toHaveBeenCalledWith('m', 'xlsx', query, 'th');
      expect(create).toHaveBeenCalledWith(blob);
      expect(clicked).toEqual([{ download: 'm-20261104-1200.xlsx', href: 'blob:u' }]);
      // 立刻 revoke 可能讓瀏覽器還沒開始下載就失效，延後釋放
      expect(revoke).not.toHaveBeenCalled();
      vi.advanceTimersByTime(1000);
      expect(revoke).toHaveBeenCalledWith('blob:u');
    } finally {
      vi.useRealTimers();
    }
  });

  it('notifies the server message when the export is rejected', async () => {
    api.exportRecords.mockRejectedValue({ status: 400, errors: { filter: ['匯出最多 10000 筆，請加篩選條件'] } });
    const w = mountMenu();
    await pick(w, 'export-csv');
    await new Promise((r) => setTimeout(r, 50));
    expect(api.exportRecords).toHaveBeenCalledWith('m', 'csv', query, 'zh-TW');
    expect(document.body.textContent).toContain('匯出最多 10000 筆，請加篩選條件');
  });
});
