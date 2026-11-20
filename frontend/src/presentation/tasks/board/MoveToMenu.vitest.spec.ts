import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { BoardCard, BoardColumn } from 'src/domain/dynamic/task-board-types';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import { normalizeBoard } from 'src/services/api/task-normalize';
import { boardWire, columnWire, cardWire, dueBoardWire } from './board-fixtures';
import MoveToMenu from './MoveToMenu.vue';

const labels: Record<string, string> = { todo: '待辦', inProgress: '進行中', done: '已完成', overdue: '逾期', today: '今天', thisWeek: '本週', later: '之後', none: '無到期日' };

function mountMenu(opts: { phone?: boolean; wire?: Record<string, unknown>; card?: Record<string, unknown> } = {}) {
  const board = normalizeBoard((opts.wire ?? boardWire()) as never);
  const columns: BoardColumn[] = board.columns;
  const card: BoardCard = columns.flatMap((c) => c.cards)[0];
  return mount(MoveToMenu, {
    attachTo: document.body,
    props: { card: { ...card, ...opts.card }, columns, groupBy: columns[0].kind === 'dueBucket' ? 'dueBucket' : 'status', labelOf: (k: string) => labels[k] ?? k, phone: !!opts.phone },
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } })] },
  });
}
/** 到期區間看板，卡片 1 在「今天」欄。 */
const dueWithCard = () => {
  const wire = dueBoardWire() as { columns: { key: string; cards: unknown[]; total: number }[] };
  wire.columns.find((c) => c.key === 'today')!.cards = [cardWire({ id: 1 })];
  return wire as unknown as Record<string, unknown>;
};
const items = () => [...document.body.querySelectorAll<HTMLElement>('[role="menuitem"]')];

describe('MoveToMenu', () => {
  afterEach(() => { document.body.innerHTML = ''; });

  it('MoveToMenu lists droppable columns and disables the rest with reasons', async () => {
    const w = mountMenu({ wire: dueWithCard() });
    await w.get('[data-testid="board-card-move-1"]').trigger('click');
    await flushPromises();
    expect(w.get('[data-testid="board-card-move-1"]').attributes('aria-haspopup')).toBe('menu');
    const byKey = (k: string) => document.body.querySelector<HTMLElement>(`[data-testid="move-item-${k}"]`)!;
    expect(items().map((i) => i.querySelector('.move-label')?.textContent)).toEqual(['逾期', '今天', '本週', '之後', '無到期日']);
    expect(byKey('overdue').getAttribute('aria-disabled')).toBe('true');
    expect(byKey('overdue').textContent).toContain('不能改成過去的日期，請開任務詳細');
    expect(byKey('later').getAttribute('aria-disabled')).toBe('false');
    expect(byKey('thisWeek').getAttribute('aria-disabled')).toBe('false');
    // 選不能放的欄什麼都不發生，選能放的欄送出並關閉
    byKey('overdue').click();
    expect(w.emitted('move')).toBeUndefined();
    byKey('later').click();
    await flushPromises();
    expect(w.emitted('move')).toEqual([['later']]);
    w.unmount();
  });

  it('shows the current column as current and not selectable', async () => {
    const w = mountMenu();
    await w.get('[data-testid="board-card-move-1"]').trigger('click');
    await flushPromises();
    const current = document.body.querySelector<HTMLElement>('[data-testid="move-item-todo"]')!;
    expect(current.getAttribute('aria-current')).toBe('true');
    expect(current.textContent).toContain('目前所在');
    w.unmount();
  });

  it('MoveToMenu is keyboard operable (Enter, arrow keys)', async () => {
    const w = mountMenu({ wire: boardWire({ columns: [
      columnWire({ key: 'todo', cards: [cardWire({ id: 1 })] }), columnWire({ key: 'inProgress', cards: [] }), columnWire({ key: 'done', cards: [] }),
    ] }) });
    const button = w.get('[data-testid="board-card-move-1"]');
    // 原生按鈕：Tab 到得了，Enter 會產生 click
    expect(button.element.tagName).toBe('BUTTON');
    expect(button.attributes('tabindex')).not.toBe('-1');
    await button.trigger('click');
    await flushPromises();
    const list = document.body.querySelector<HTMLElement>('[role="menu"]')!;
    const press = (key: string) => list.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }));
    // 目前所在的「待辦」不能選，第一個可選的是「進行中」，選單開啟後焦點在它上面（data-autofocus）
    await new Promise((r) => setTimeout(r, 50));
    expect(document.activeElement).toBe(items()[1]);
    press('ArrowDown');
    expect(document.activeElement).toBe(items()[2]);
    press('ArrowDown');
    expect(document.activeElement).toBe(items()[0]);
    press('ArrowUp');
    expect(document.activeElement).toBe(items()[2]);
    press('Home');
    expect(document.activeElement).toBe(items()[0]);
    press('End');
    expect(document.activeElement).toBe(items()[2]);
    // 項目是原生按鈕：Enter 與空白鍵會變成 click
    expect(items().every((i) => i.tagName === 'BUTTON')).toBe(true);
    items()[2].click();
    await flushPromises();
    expect(w.emitted('move')).toEqual([['done']]);
    expect(document.body.querySelector('[role="menu"]')).toBeNull();
    w.unmount();
  });

  it('opens a full-width bottom sheet on a phone', async () => {
    const w = mountMenu({ phone: true });
    await w.get('[data-testid="board-card-move-1"]').trigger('click');
    await flushPromises();
    const sheet = document.body.querySelector('[data-testid="move-sheet"]')!;
    expect(sheet.classList.contains('full-width')).toBe(true);
    expect(sheet.textContent).toContain('到…');
    expect(sheet.querySelectorAll('[role="menuitem"]').length).toBe(3);
    w.unmount();
  });

  it('a disabled column does not close the menu; Esc still does', async () => {
    const w = mountMenu({ wire: dueWithCard() });
    await w.get('[data-testid="board-card-move-1"]').trigger('click');
    await flushPromises();
    document.body.querySelector<HTMLElement>('[data-testid="move-item-overdue"]')!.click();
    await flushPromises();
    expect(document.body.querySelector('[role="menu"]')).not.toBeNull();
    expect(w.emitted('move')).toBeUndefined();
    w.unmount();
  });

  it('only the current item is a Tab stop (roving tabindex), and Tab closes the menu', async () => {
    const w = mountMenu({ wire: dueWithCard() });
    await w.get('[data-testid="board-card-move-1"]').trigger('click');
    await new Promise((r) => setTimeout(r, 50));
    const tabbable = () => items().filter((i) => i.getAttribute('tabindex') !== '-1');
    expect(tabbable()).toEqual([document.activeElement]);
    const list = document.body.querySelector<HTMLElement>('[role="menu"]')!;
    list.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true, cancelable: true }));
    await flushPromises();
    expect(tabbable()).toEqual([document.activeElement]);
    expect(tabbable()).toHaveLength(1);
    list.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true }));
    await flushPromises();
    expect(document.body.querySelector('[role="menu"]')).toBeNull();
    w.unmount();
  });

  it('the button says what it opens: a menu on desktop, a dialog on a phone', async () => {
    const desktop = mountMenu();
    expect(desktop.get('[data-testid="board-card-move-1"]').attributes('aria-haspopup')).toBe('menu');
    desktop.unmount();
    const phone = mountMenu({ phone: true });
    expect(phone.get('[data-testid="board-card-move-1"]').attributes('aria-haspopup')).toBe('dialog');
    phone.unmount();
  });

  it('the sheet title reuses the translated "move X to" sentence instead of gluing words with a full-width space', async () => {
    const w = mountMenu({ phone: true, card: { title: '寄合約' } });
    await w.get('[data-testid="board-card-move-1"]').trigger('click');
    await flushPromises();
    const sheet = document.body.querySelector('[data-testid="move-sheet"]')!;
    expect(sheet.textContent).toContain('移動「寄合約」到…');
    expect(sheet.textContent).not.toContain('\u3000');
    w.unmount();
  });
});
