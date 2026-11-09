import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi, type DemoDataStatus } from 'src/services/api/dynamic-api';
import DemoDataPage from './DemoDataPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: { getDemoDataStatus: vi.fn(), generateDemoData: vi.fn(), getMe: vi.fn() },
}));
const api = vi.mocked(dynamicApi);

const status = (s: Partial<DemoDataStatus>): DemoDataStatus => ({ state: 'notGenerated', conflicts: [], done: 0, total: 0, ...s });

let mounted: ReturnType<typeof mount> | undefined;

async function mountPage(s: DemoDataStatus, add = true, edit = true) {
  api.getMe.mockResolvedValue({ designer: { read: true, add, edit, delete: true } } as never);
  api.getDemoDataStatus.mockResolvedValue(s);
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const Host = { render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(DemoDataPage))) };
  const w = mount(Host, {
    attachTo: document.body,
    global: {
      plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], i18n, createPinia(),
        [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]],
    },
  });
  await flushPromises();
  mounted = w;
  return w;
}
const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLButtonElement | null;
const text = (id: string) => q(id)?.textContent?.trim();

describe('DemoDataPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  afterEach(() => {
    mounted?.unmount();
  });

  it('notGenerated: shows description and an enabled button that starts generation', async () => {
    api.generateDemoData.mockResolvedValue(status({ state: 'running' }));
    const w = await mountPage(status({}));
    expect(text('demo-state')).toBe('尚未產生');
    expect(w.text()).toContain('開啟 MES 模具廠、CRM、進銷存三個內建應用程式');
    expect(q('demo-generate')!.disabled).toBe(false);
    q('demo-generate')!.click();
    await flushPromises();
    expect(api.generateDemoData).toHaveBeenCalledTimes(1);
    expect(document.body.textContent).toContain('已開始產生 Demo 資料');
  });

  it('running: shows n／N and a loading, disabled button', async () => {
    await mountPage(status({ state: 'running', done: 120, total: 480 }));
    expect(text('demo-state')).toBe('產生中（120／480）');
    expect(q('demo-generate')!.disabled).toBe(true);
    expect(q('demo-generate')!.querySelector('.q-spinner')).not.toBeNull();
  });

  it('running with total 0: shows only 產生中', async () => {
    await mountPage(status({ state: 'running', done: 0, total: 0 }));
    expect(text('demo-state')).toBe('產生中');
  });

  it('generated: shows finish time when present, button disabled', async () => {
    await mountPage(status({ state: 'generated', finishedAt: '2026-11-05T08:30:00Z' }));
    expect(text('demo-state')).toBe('已產生');
    expect(text('demo-finished')).toContain('完成時間');
    expect(q('demo-generate')!.disabled).toBe(true);
  });

  it('generated derived from the database has no times or progress', async () => {
    await mountPage(status({ state: 'generated' }));
    expect(text('demo-state')).toBe('已產生');
    expect(q('demo-finished')).toBeNull();
  });

  it('conflict: lists every conflict with the hint, button disabled', async () => {
    await mountPage(status({ state: 'conflict', conflicts: ['模組代碼 order 已存在', '既有實體 test.entity 已有擴充模組'] }));
    expect([...q('demo-conflicts')!.querySelectorAll('li')].map((li) => li.textContent)).toEqual(['模組代碼 order 已存在', '既有實體 test.entity 已有擴充模組']);
    expect(text('demo-conflict-hint')).toContain('不會產生 Demo 資料');
    expect(q('demo-generate')!.disabled).toBe(true);
  });

  it('failed: shows the message and allows a retry', async () => {
    await mountPage(status({ state: 'failed', message: '資料庫逾時' }));
    expect(text('demo-state')).toBe('產生失敗');
    expect(text('demo-message')).toBe('資料庫逾時');
    expect(q('demo-generate')!.disabled).toBe(false);
  });

  it('409 from the POST shows the server message and refetches the status', async () => {
    api.generateDemoData.mockRejectedValue({ status: 409, message: '已有同代碼的模組，未產生 Demo 資料' });
    await mountPage(status({}));
    api.getDemoDataStatus.mockResolvedValue(status({ state: 'conflict', conflicts: ['模組代碼 order 已存在'] }));
    q('demo-generate')!.click();
    await flushPromises();
    expect(document.body.textContent).toContain('已有同代碼的模組，未產生 Demo 資料');
    expect(text('demo-conflicts')).toContain('模組代碼 order 已存在');
  });

  it.each([[false, true], [true, false]])('without designer add/edit (add=%s edit=%s): status shown, button disabled', async (add, edit) => {
    await mountPage(status({}), add, edit);
    expect(text('demo-state')).toBe('尚未產生');
    expect(q('demo-generate')!.disabled).toBe(true);
  });
});
