import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { defineComponent, h, ref } from 'vue';
import { createI18n } from 'vue-i18n';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { useRecipientNames } from './useRecipientNames';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { getUsers: vi.fn(), getGroups: vi.fn() } }));
const api = vi.mocked(dynamicApi);

function run(enabled?: () => boolean) {
  let result!: ReturnType<typeof useRecipientNames>;
  const C = defineComponent({ setup() { result = useRecipientNames(enabled); return () => h('div'); } });
  const w = mount(C, {
    global: { plugins: [createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } }),
      [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]] },
  });
  return { w, get r() { return result; } };
}

describe('useRecipientNames', () => {
  beforeEach(() => { vi.clearAllMocks(); });

  it('sends no request when disabled', async () => {
    const h = run(() => false);
    await flushPromises();
    expect(api.getUsers).not.toHaveBeenCalled();
    expect(api.getGroups).not.toHaveBeenCalled();
    expect(h.r.ready.value).toBe(false);
    h.w.unmount();
  });

  it('names and ready stay unset until both lists load', async () => {
    api.getUsers.mockResolvedValue([{ userId: 'u1', displayName: '王小明' }] as never);
    api.getGroups.mockRejectedValue(new Error('boom'));
    const h = run();
    await flushPromises();
    expect(h.r.ready.value).toBe(false);
    expect(h.r.names.value).toBeUndefined();
    h.w.unmount();
  });

  it('is ready once users and groups both load, with the user option label from i18n', async () => {
    api.getUsers.mockResolvedValue([{ userId: 'u1', displayName: '王小明' }] as never);
    api.getGroups.mockResolvedValue([]);
    const h = run();
    await flushPromises();
    expect(h.r.ready.value).toBe(true);
    expect(h.r.names.value?.get('user:u1')).toBe('王小明（u1）');
    h.w.unmount();
  });
});
