import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { nextTick } from 'vue';
import { createI18n } from 'vue-i18n';
import type { ListView } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import ViewMenu from './ViewMenu.vue';
import { FACTORY_ME_THEME } from 'src/domain/theme/theme-fixtures';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { getMe: vi.fn() } }));
const designer = (edit: boolean) => ({
  userId: 'u', displayName: 'U', isSystemAdmin: false, mustChangePassword: false, groups: [], designer: { read: true, add: edit, edit, delete: edit }, theme: FACTORY_ME_THEME,
});
let canEditDesigner = true;

const view = (p: Partial<ListView>): ListView => ({
  id: 1, name: { 'zh-TW': '甲' }, shared: false, isOwner: true, columns: [1], sort: [], isDefault: false, staleFieldIds: [], ...p,
});

async function open(current: ListView | undefined, modified = false) {
  vi.mocked(dynamicApi.getMe).mockResolvedValue(designer(canEditDesigner));
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(ViewMenu, {
    props: { views: current ? [current] : [], current, modified },
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Dialog: quasar.Dialog } }], i18n, createPinia(), [VueQueryPlugin, { queryClient: new QueryClient() }]] },
    attachTo: document.body,
  });
  await flushPromises();
  await w.get('[data-testid="view-btn"]').trigger('click');
  await nextTick();
  await new Promise((r) => setTimeout(r, 50));
  return w;
}
const item = (id: string) => document.body.querySelector(`[data-testid="${id}"]`);

describe('ViewMenu', () => {
  beforeEach(() => {
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
    document.body.innerHTML = '';
    canEditDesigner = true;
  });

  it("hides update for others' personal view", async () => {
    const w = await open(view({ isOwner: false, shared: false }));
    expect(item('view-save-as')).not.toBeNull();
    expect(item('view-update')).toBeNull();
    expect(item('view-delete')).toBeNull();
    w.unmount();
  });

  it("shows update on others' shared view only with designer edit", async () => {
    const shared = view({ isOwner: false, shared: true });
    const w1 = await open(shared);
    expect(item('view-update')).not.toBeNull();
    w1.unmount();
    document.body.innerHTML = '';
    canEditDesigner = false;
    const w2 = await open(shared);
    expect(item('view-update')).toBeNull();
    expect(item('view-set-default')).toBeNull();
    expect(item('view-delete')).toBeNull();
    w2.unmount();
  });

  it('shows update for own view and confirms delete through the Quasar Dialog', async () => {
    const w = await open(view({}), true);
    expect(item('view-update')).not.toBeNull();
    (item('view-delete') as HTMLElement).click();
    await new Promise((r) => setTimeout(r, 50));
    await new Promise((r) => setTimeout(r, 50));
    expect(document.body.textContent).toContain('確定要刪除這個檢視嗎？');
    expect(w.emitted('remove')).toBeUndefined();
    const ok = [...document.body.querySelectorAll('.q-dialog button')].at(-1) as HTMLElement;
    ok.click();
    await new Promise((r) => setTimeout(r, 50));
    expect(w.emitted('remove')).toHaveLength(1);
    expect(w.text()).toContain('已修改');
    w.unmount();
  });

  it('ViewMenu hides the shared checkbox without designer edit', async () => {
    const dialogText = async (edit: boolean) => {
      canEditDesigner = edit;
      const w = await open(undefined);
      (item('view-save-as') as HTMLElement).click();
      await new Promise((r) => setTimeout(r, 100));
      const text = document.body.querySelector('.q-dialog')?.textContent ?? '';
      w.unmount();
      document.body.innerHTML = '';
      return text;
    };
    expect(await dialogText(true)).toContain(zhTW.dynamic.view.shared);
    expect(await dialogText(false)).not.toContain(zhTW.dynamic.view.shared);
  });
});
