import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { DrillResult } from 'src/domain/dynamic/report-types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import DrillDialog from './DrillDialog.vue';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { drillReport: vi.fn() } }));
const api = vi.mocked(dynamicApi);

const RESULT: DrillResult = {
  state: 'ok', rowMeaning: '一張訂單', basisDate: '2026-11-05', dedupedNote: true, truncated: true,
  columns: [{ key: 'main:5:', label: '單號', role: 'field', valueType: 'text' }], detailRows: [['SO-1']],
};
const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`);
let mounted: ReturnType<typeof mount> | undefined;

async function open(groupKeys: string[] | null) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  mounted = mount(DrillDialog, {
    props: { reportId: 3, groupKeys },
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar }], i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
  });
  await flushPromises();
}

describe('DrillDialog', () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(() => { mounted?.unmount(); document.body.innerHTML = ''; });

  it('drill truncated shows notice, detail rows and the dedupe note', async () => {
    api.drillReport.mockResolvedValue(RESULT);
    await open(['a']);
    expect(api.drillReport).toHaveBeenCalledWith(3, ['a'], null, 'zh-TW');
    expect(q('drill-truncated')!.textContent).toContain('500');
    expect(q('report-detail-table')!.textContent).toContain('SO-1');
    expect(q('deduped-note')).not.toBeNull();
  });

  it('does not truncate-warn when the result is complete, and does not query while closed', async () => {
    api.drillReport.mockResolvedValue({ ...RESULT, truncated: false });
    await open(['a']);
    expect(q('drill-truncated')).toBeNull();
    mounted!.unmount();
    document.body.innerHTML = '';
    api.drillReport.mockClear();
    await open(null);
    expect(api.drillReport).not.toHaveBeenCalled();
  });
});
