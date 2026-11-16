import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import zhTW from 'src/i18n/zh-TW.json';
import DesignerToolbar from './DesignerToolbar.vue';

const base = { readonly: false, phone: false, showFlowProps: false, undoable: false, redoable: false, dirty: false, busy: false, errorCount: 0, canDiscard: false, canDelete: false };
function mountBar(props: Partial<typeof base> = {}) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  return mount(DesignerToolbar, { props: { ...base, ...props }, attachTo: document.body, global: { plugins: [[quasar.Quasar, { components: quasar }], i18n] } });
}
beforeEach(() => { document.body.innerHTML = ''; });

describe('DesignerToolbar', () => {
  it('a read-only user only gets fit', () => {
    const w = mountBar({ readonly: true });
    expect(w.find('[data-testid="tb-fit"]').exists()).toBe(true);
    for (const id of ['tb-save', 'tb-publish', 'tb-validate', 'tb-undo', 'tb-more']) expect(w.find(`[data-testid="${id}"]`).exists()).toBe(false);
  });

  it('save is highlighted only when there are unsaved changes', () => {
    expect(mountBar().get('[data-testid="tb-save"]').classes()).not.toContain('bg-primary');
    expect(mountBar({ dirty: true }).get('[data-testid="tb-save"]').classes()).toContain('bg-primary');
  });

  it('the error badge shows the count and opens the list', async () => {
    const w = mountBar({ errorCount: 3 });
    expect(w.get('[data-testid="tb-errors"]').text()).toContain('3');
    await w.get('[data-testid="tb-errors"]').trigger('click');
    expect(w.emitted('issues')).toHaveLength(1);
    expect(mountBar().find('[data-testid="tb-errors"]').exists()).toBe(false);
  });

  it('save, validate and publish emit; busy disables them', async () => {
    const w = mountBar();
    await w.get('[data-testid="tb-save"]').trigger('click');
    await w.get('[data-testid="tb-validate"]').trigger('click');
    await w.get('[data-testid="tb-publish"]').trigger('click');
    expect(['save', 'validate', 'publish'].map((e) => w.emitted(e)?.length)).toEqual([1, 1, 1]);
    const busy = mountBar({ busy: true });
    expect(busy.get('[data-testid="tb-publish"]').attributes('disabled')).toBeDefined();
  });

  it('a flow with a published version and a draft offers discard in the more menu', async () => {
    const w = mountBar({ canDiscard: true });
    await w.get('[data-testid="tb-more"]').trigger('click');
    await new Promise((r) => setTimeout(r, 50));
    expect(document.body.querySelector('[data-testid="tb-discard"]')).not.toBeNull();
    expect(document.body.querySelector('[data-testid="tb-delete"]')).toBeNull();
    w.unmount();
  });

  it('a draft-only flow shows delete flow instead of discard (U64)', async () => {
    const w = mountBar({ canDelete: true });
    await w.get('[data-testid="tb-more"]').trigger('click');
    await new Promise((r) => setTimeout(r, 50));
    expect(document.body.querySelector('[data-testid="tb-delete"]')?.textContent).toContain('刪除流程');
    expect(document.body.querySelector('[data-testid="tb-discard"]')).toBeNull();
    w.unmount();
  });

  it('a flow with neither has no more menu', () => {
    expect(mountBar().find('[data-testid="tb-more"]').exists()).toBe(false);
  });
});
