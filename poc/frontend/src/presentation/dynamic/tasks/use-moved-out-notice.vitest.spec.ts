import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { describe, expect, it, vi } from 'vitest';
import { defineComponent } from 'vue';
import { createI18n } from 'vue-i18n';
import zhTW from 'src/i18n/zh-TW.json';
import { bodyOf, HttpFail, mockHttp, type Call } from 'src/test-utils/mock-http';
import { taskDetailWire } from './task-fixtures';
import { useMovedOutNotice } from './use-moved-out-notice';

let calls: Call[];
let notice!: ReturnType<typeof useMovedOutNotice>;
let invalidated: () => string[];

function mountHost(undoReply: unknown = taskDetailWire({ id: 5 })) {
  calls = mockHttp((c) => (c.url === '/tasks/5/undo-reassign' ? undoReply : []));
  const qc = new QueryClient();
  const spy = vi.spyOn(qc, 'invalidateQueries');
  invalidated = () => spy.mock.calls.map((c) => JSON.stringify((c[0] as { queryKey: unknown[] }).queryKey));
  return mount(defineComponent({ setup() { notice = useMovedOutNotice(); return () => null; } }), {
    attachTo: document.body,
    global: {
      plugins: [
        [quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }],
        createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } }),
        [VueQueryPlugin, { queryClient: qc }],
      ],
    },
  });
}

/** Quasar renders a toast a tick after notify(). */
const settle = async () => { await flushPromises(); await new Promise((r) => setTimeout(r, 30)); await flushPromises(); };
const toasts = () => [...document.body.querySelectorAll('.q-notification')].map((n) => n.textContent ?? '');
const newest = () => [...document.body.querySelectorAll<HTMLElement>('.q-notification')].filter((n) => n.textContent?.includes('已移出你的範圍')).at(-1)!;
const undoButton = (toast = newest()) => [...toast.querySelectorAll<HTMLElement>('.q-btn')].find((b) => b.textContent?.includes('復原'));

// Notify keeps one document-level container for the whole file, so the body is never reset; each test looks at the newest toast.
describe('useMovedOutNotice', () => {

  it('moved-out notice calls undo with the token and refreshes tasks', async () => {
    const w = mountHost();
    notice.show(5, 'tok-1');
    await settle();
    expect(toasts().some((t) => t.includes('已移出你的範圍') && t.includes('復原'))).toBe(true);
    undoButton()!.click();
    await settle();
    const post = calls.find((c) => c.url === '/tasks/5/undo-reassign');
    expect(post?.method).toBe('post');
    expect(bodyOf(post)).toEqual({ undoToken: 'tok-1' });
    expect(toasts().some((t) => t.includes('已復原'))).toBe(true);
    expect(invalidated()).toEqual(expect.arrayContaining(['["tasks"]', '["task",5]']));
    w.unmount();
  });

  it('shows the failure message when undo returns 400', async () => {
    const w = mountHost(new HttpFail(400, { errors: { undoToken: ['這次變更已無法復原'] } }));
    notice.show(5, 'tok-2');
    await flushPromises();
    undoButton()!.click();
    await settle();
    expect(toasts().some((t) => t.includes('這次變更已無法復原'))).toBe(true);
    w.unmount();
  });

  it('without a token it only informs (nothing to undo)', async () => {
    const w = mountHost();
    notice.show(5, null);
    await settle();
    expect(newest()).toBeDefined();
    expect(undoButton(newest())).toBeUndefined();
    w.unmount();
  });
});
