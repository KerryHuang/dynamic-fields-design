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

async function open(groupKeys: string[] | null, extra: Record<string, unknown> = {}) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  mounted = mount(DrillDialog, {
    props: { reportId: 3, groupKeys, ...extra },
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
    expect(api.drillReport).toHaveBeenCalledWith(3, ['a'], null, 'zh-TW', { range: null, departmentIds: [], includeSubDepartments: true });
    expect(q('drill-truncated')!.textContent).toContain('明細太多，只顯示前 1 列');
    expect(q('report-detail-table')!.textContent).toContain('SO-1');
    expect(q('deduped-note')).not.toBeNull();
  });

  it('截斷訊息帶實際列數', async () => {
    api.drillReport.mockResolvedValue({ ...RESULT, detailRows: [['SO-1'], ['SO-2'], ['SO-3']] });
    await open(['a']);
    expect(q('drill-truncated')!.textContent).toContain('明細太多，只顯示前 3 列');
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

  it('passes the page filter to the drill query and emits the task id of a clicked row', async () => {
    api.drillReport.mockResolvedValue({ ...RESULT, taskIds: [77] });
    const filter = { range: { kind: 'thisMonth', n: null }, departmentIds: [4, 5], includeSubDepartments: false };
    await open(['a'], { filter, openableTasks: true });
    expect(api.drillReport).toHaveBeenCalledWith(3, ['a'], { kind: 'thisMonth', n: null }, 'zh-TW', filter);
    (q('detail-row-0') as HTMLElement).click();
    expect(mounted!.emitted('openTask')).toEqual([[77]]);
  });
});
