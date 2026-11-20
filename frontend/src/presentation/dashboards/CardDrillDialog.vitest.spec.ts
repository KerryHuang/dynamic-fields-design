import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { DrillResult } from 'src/domain/dynamic/report-types';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import CardDrillDialog from './CardDrillDialog.vue';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { drillCard: vi.fn() } }));
const api = vi.mocked(dynamicApi);

const RESULT: DrillResult = {
  state: 'ok', rowMeaning: '一張訂單', basisDate: '2026-11-05', dedupedNote: false, truncated: true,
  columns: [{ key: 'main:5:', label: '單號', role: 'field', valueType: 'text' }], detailRows: [['SO-1'], ['SO-2'], ['SO-3']],
};
const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`);
let mounted: ReturnType<typeof mount> | undefined;

async function open() {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  mounted = mount(CardDrillDialog, {
    props: { dashboardId: 1, cardId: 'c', groupKeys: ['a'], filterRange: null },
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar }], i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
  });
  await flushPromises();
}

describe('CardDrillDialog', () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(() => { mounted?.unmount(); document.body.innerHTML = ''; });

  it('截斷訊息帶實際列數', async () => {
    api.drillCard.mockResolvedValue(RESULT);
    await open();
    expect(q('card-drill-truncated')!.textContent).toContain('明細太多，只顯示前 3 列');
  });

  it('沒有截斷時不顯示', async () => {
    api.drillCard.mockResolvedValue({ ...RESULT, truncated: false });
    await open();
    expect(q('card-drill-truncated')).toBeNull();
  });
});
