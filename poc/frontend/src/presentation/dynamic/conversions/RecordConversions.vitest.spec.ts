import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import type { RecordConversion } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import RecordConversions from './RecordConversions.vue';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { getRecordConversions: vi.fn() } }));
const api = vi.mocked(dynamicApi);

const blocked: RecordConversion = {
  key: 'q2o', name: { 'zh-TW': '報價轉訂單' }, targetModuleKey: 'order', targetName: { 'zh-TW': '訂單' },
  allowed: false, reason: '應用程式已關閉，資料唯讀', targets: [{ id: 9, title: 'SO-1', moduleKey: 'order' }], lines: [],
};

async function mountList(closed: boolean) {
  api.getRecordConversions.mockResolvedValue([blocked]);
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/:p(.*)*', component: { render: () => null } }] });
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(RecordConversions, {
    props: { recordId: 5, rowVersion: 'rv1', closed },
    global: { plugins: [[quasar.Quasar, { components: quasar }], createPinia(), router, i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
  });
  await flushPromises();
  return w;
}

describe('RecordConversions', () => {
  beforeEach(() => vi.clearAllMocks());

  it('shows why a conversion is unavailable', async () => {
    const w = await mountList(false);
    expect(w.find('[data-testid="convert-reason-q2o"]').text()).toContain('應用程式已關閉');
  });

  it('does not repeat the closed-app reason the page banner already shows, but keeps the converted documents', async () => {
    const w = await mountList(true);
    expect(w.find('[data-testid="convert-reason-q2o"]').exists()).toBe(false);
    expect(w.find('[data-testid="convert-targets-q2o"]').text()).toContain('SO-1');
  });
});
