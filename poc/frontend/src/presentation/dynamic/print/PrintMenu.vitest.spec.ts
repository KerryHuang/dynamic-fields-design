import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import PrintMenu from './PrintMenu.vue';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { getPrintTemplates: vi.fn(), printRecord: vi.fn() } }));
const api = vi.mocked(dynamicApi);

async function mountMenu(props: { grant?: boolean }) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(PrintMenu, {
    props: { moduleKey: 'quote', recordId: 5, ...props },
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
  });
  await flushPromises();
  return w;
}

describe('PrintMenu', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    api.getPrintTemplates.mockResolvedValue([{ key: 'sheet', name: { 'zh-TW': '報價單' } }]);
  });

  it('PrintMenu passes recordId under grant', async () => {
    const w = await mountMenu({ grant: true });
    expect(api.getPrintTemplates).toHaveBeenCalledWith('quote', 5);
    expect(w.find('[data-testid="print-menu"]').exists()).toBe(true);
  });

  it('does not pass recordId on the normal path', async () => {
    await mountMenu({});
    expect(api.getPrintTemplates).toHaveBeenCalledWith('quote', undefined);
  });

  it('shows nothing when the backend lists no templates', async () => {
    api.getPrintTemplates.mockResolvedValue([]);
    const w = await mountMenu({ grant: true });
    expect(w.find('[data-testid="print-menu"]').exists()).toBe(false);
  });
});
