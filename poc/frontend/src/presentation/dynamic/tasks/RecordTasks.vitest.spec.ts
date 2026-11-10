import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import zhTW from 'src/i18n/zh-TW.json';
import { useIdentityStore } from 'src/stores/use-identity-store';
import { bodyOf, HttpFail, mockHttp, type Call } from 'src/test-utils/mock-http';
import RecordTasks from './RecordTasks.vue';
import { taskWire, USERS } from './task-fixtures';

let calls: Call[];

async function mountBlock(props: { closed: boolean }, items: unknown[], canAdd = true, patchFails = false) {
  const pinia = createPinia();
  setActivePinia(pinia);
  useIdentityStore().setSession('sales01');
  calls = mockHttp((c) => {
    if (c.url === '/records/9/tasks' && c.method === 'get') return { items, canAdd };
    if (c.url === '/users') return USERS;
    if (c.url === '/records/9/tasks' && c.method === 'post') return taskWire({ id: 50 });
    if (c.method === 'patch') return patchFails ? new HttpFail(409, { message: '應用程式已關閉' }) : taskWire({ status: 'done' });
    return [];
  });
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/:p(.*)*', component: { render: () => null } }] });
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(RecordTasks, {
    props: { recordId: 9, ...props },
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], pinia, router, i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
  });
  await flushPromises();
  return w;
}

describe('RecordTasks', () => {
  beforeEach(() => { document.body.innerHTML = ''; });

  it('未指派的任務顯示「未指派」，逾期的到期日有 text-negative', async () => {
    const w = await mountBlock({ closed: false }, [
      taskWire({ id: 1, assigneeUserId: undefined, assigneeName: undefined, assigneeActive: false }),
      taskWire({ id: 2, overdue: true, dueDate: '2026-10-01' }),
    ]);
    expect(w.get('[data-testid="task-row-1"]').text()).toContain('未指派');
    expect(w.get('[data-testid="task-due-2"]').classes()).toContain('text-negative');
    expect(w.get('[data-testid="task-due-1"]').classes()).not.toContain('text-negative');
    w.unmount();
  });

  it('勾選完成送 PATCH /tasks/5 {status:"done"}', async () => {
    const w = await mountBlock({ closed: false }, [taskWire()]);
    await w.get('[data-testid="task-check-5"]').trigger('click');
    await flushPromises();
    const patch = calls.find((c) => c.method === 'patch');
    expect(patch?.url).toBe('/tasks/5');
    expect(bodyOf(patch)).toEqual({ status: 'done' });
    w.unmount();
  });

  it('closed=true 時沒有新增鈕，勾選停用', async () => {
    const w = await mountBlock({ closed: true }, [taskWire()]);
    expect(w.find('[data-testid="task-add"]').exists()).toBe(false);
    expect(w.get('[data-testid="task-check-5"]').attributes('aria-disabled')).toBe('true');
    w.unmount();
  });

  it('canAdd 為 false 時沒有新增鈕；沒有編輯權限的任務不能勾選', async () => {
    const w = await mountBlock({ closed: false }, [taskWire({ canEdit: false })], false);
    expect(w.find('[data-testid="task-add"]').exists()).toBe(false);
    expect(w.get('[data-testid="task-check-5"]').attributes('aria-disabled')).toBe('true');
    w.unmount();
  });

  it('有 canAdd 且未關閉時顯示新增鈕；沒有任務時顯示空白說明', async () => {
    const w = await mountBlock({ closed: false }, []);
    expect(w.find('[data-testid="task-add"]').exists()).toBe(true);
    expect(w.text()).toContain('目前沒有任務');
    w.unmount();
  });

  it('取消勾選已完成的任務送 {status:"todo"}', async () => {
    const w = await mountBlock({ closed: false }, [taskWire({ status: 'done' })]);
    await w.get('[data-testid="task-check-5"]').trigger('click');
    await flushPromises();
    expect(bodyOf(calls.find((c) => c.method === 'patch'))).toEqual({ status: 'todo' });
    w.unmount();
  });

  it('勾選失敗時跳出錯誤提示', async () => {
    const w = await mountBlock({ closed: false }, [taskWire()], true, true);
    const notify = vi.spyOn(w.vm.$q, 'notify');
    await w.get('[data-testid="task-check-5"]').trigger('click');
    await flushPromises();
    expect(notify).toHaveBeenCalledWith(expect.objectContaining({ type: 'negative', message: '應用程式已關閉' }));
    w.unmount();
  });

  it('在記錄上新增任務時負責人預設為目前使用者', async () => {
    const w = await mountBlock({ closed: false }, []);
    await w.get('[data-testid="task-add"]').trigger('click');
    await flushPromises();
    const title = document.body.querySelector<HTMLInputElement>('input[data-testid="task-form-title"]')!;
    title.value = '回電';
    title.dispatchEvent(new Event('input'));
    await flushPromises();
    document.body.querySelector<HTMLElement>('[data-testid="task-form-submit"]')!.click();
    await flushPromises();
    const post = calls.find((c) => c.url === '/records/9/tasks' && c.method === 'post');
    expect(bodyOf(post)).toMatchObject({ title: '回電', assigneeUserId: 'sales01' });
    w.unmount();
  });
});
