import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import zhTW from 'src/i18n/zh-TW.json';
import RowActionMenu from './RowActionMenu.vue';

describe('RowActionMenu', () => {
  it('opens a menu, runs the chosen action, and the button click does not bubble to the row', async () => {
    const run = vi.fn();
    const rowClick = vi.fn();
    const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
    const Host = {
      components: { RowActionMenu },
      setup: () => ({ run, rowClick, actions: [{ label: '上移', run }, { label: '刪除', run: () => undefined, negative: true, disable: true, testid: 'del' }] }),
      template: '<div @click="rowClick"><RowActionMenu :actions="actions" testid="more" /></div>',
    };
    const w = mount(Host, { attachTo: document.body, global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], i18n] } });
    (document.body.querySelector('[data-testid="more"]') as HTMLElement).click();
    await flushPromises();
    expect(rowClick).not.toHaveBeenCalled();
    const items = [...document.body.querySelectorAll('.q-item')] as HTMLElement[];
    expect(items.map((i) => i.textContent?.trim())).toEqual(['上移', '刪除']);
    expect(items[1]!.classList.contains('text-negative')).toBe(true);
    expect(items[1]!.classList.contains('disabled')).toBe(true);
    items[0]!.click();
    await flushPromises();
    expect(run).toHaveBeenCalledTimes(1);
    w.unmount();
    document.body.innerHTML = '';
  });
});
