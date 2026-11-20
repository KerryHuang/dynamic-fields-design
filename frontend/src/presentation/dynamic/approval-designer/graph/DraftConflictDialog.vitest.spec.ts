import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import DraftConflictDialog from './DraftConflictDialog.vue';
import type { Conflict } from './use-designer-actions';

const el = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
function show(conflict: Conflict | null) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  return mount(DraftConflictDialog, { props: { conflict, busy: false }, attachTo: document.body, global: { plugins: [[quasar.Quasar, { components: quasar }], i18n] } });
}
beforeEach(() => { document.body.innerHTML = ''; });

describe('DraftConflictDialog', () => {
  it('names the editor and the time, and offers overwrite, reload and cancel', async () => {
    const w = show({ action: 'save', info: { draftUpdatedByName: 'Amy', draftUpdatedTime: '2026-11-16T08:00:00Z', draftRev: 'r9' } });
    await new Promise((r) => setTimeout(r, 30));
    expect(el('conflict-body')?.textContent).toMatch(/^Amy 在 .+ 更新了這條流程$/);
    expect(el('conflict-overwrite')?.textContent).toContain('覆蓋');
    el('conflict-overwrite')!.click();
    el('conflict-reload')!.click();
    el('conflict-cancel')!.click();
    expect(w.emitted('resolve')!.map((e) => e[0])).toEqual(['overwrite', 'reload', 'cancel']);
    w.unmount();
  });

  it('falls back to the user id, then to a nameless sentence', async () => {
    const w = show({ action: 'save', info: { draftUpdatedBy: 'u7', draftUpdatedTime: '2026-11-16T08:00:00Z' } });
    await new Promise((r) => setTimeout(r, 30));
    expect(el('conflict-body')?.textContent).toContain('u7 在');
    await w.setProps({ conflict: { action: 'save', info: {} } });
    expect(el('conflict-body')?.textContent).toContain('有人在');
    w.unmount();
  });

  it('a conflict on discard labels the main button "still discard"', async () => {
    const w = show({ action: 'discard', info: { draftUpdatedByName: 'Amy' } });
    await new Promise((r) => setTimeout(r, 30));
    expect(el('conflict-overwrite')?.textContent).toContain('仍要捨棄');
    w.unmount();
  });
});
