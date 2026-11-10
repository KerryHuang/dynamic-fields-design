import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import zhTW from 'src/i18n/zh-TW.json';
import { bodyOf, mockHttp, type Call } from 'src/test-utils/mock-http';
import RecordTasks from './RecordTasks.vue';
import { taskWire, USERS } from './task-fixtures';

let calls: Call[];

async function mountBlock(props: { closed: boolean }, items: unknown[], canAdd = true) {
  setActivePinia(createPinia());
  calls = mockHttp((c) => {
    if (c.url === '/records/9/tasks') return { items, canAdd };
    if (c.url === '/users') return USERS;
    if (c.method === 'patch') return taskWire({ status: 'done' });
    return [];
  });
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/:p(.*)*', component: { render: () => null } }] });
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(RecordTasks, {
    props: { recordId: 9, ...props },
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], createPinia(), router, i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
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
});
