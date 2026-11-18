import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { effectScope, ref } from 'vue';
import { dragModeOf, useCardDrag, type DragOptions } from './use-card-drag';

const fire = (type: string, x = 0, y = 0, extra: Record<string, unknown> = {}) => {
  const { buttons = 1, ...rest } = extra;
  const e = new MouseEvent(type, { clientX: x, clientY: y, buttons: buttons as number, bubbles: true, cancelable: true });
  Object.assign(e, { pointerType: 'mouse', pointerId: 1, isPrimary: true }, rest);
  window.dispatchEvent(e);
  return e;
};
const down = (pointerType: string, x = 0, y = 0) => ({ pointerType, pointerId: 1, isPrimary: true, button: 0, clientX: x, clientY: y, target: document.body } as unknown as PointerEvent);

function setup(o: Partial<DragOptions> = {}) {
  const dropped: [number, string][] = [];
  const scope = effectScope();
  const drag = scope.run(() => useCardDrag({ enabled: true, hitTest: () => null, onDrop: (id, key) => dropped.push([id, key]), ...o }))!;
  return { drag, dropped, stop: () => scope.stop() };
}

describe('use-card-drag', () => {
  beforeEach(() => { vi.useFakeTimers(); });
  afterEach(() => { vi.runOnlyPendingTimers(); vi.useRealTimers(); });

  it('use-card-drag starts on mouse move past threshold', () => {
    const { drag, dropped, stop } = setup({ hitTest: () => 'done' });
    drag.start(down('mouse', 10, 10), 7);
    fire('pointermove', 12, 12);
    expect(drag.state.value).toBeNull();
    fire('pointermove', 30, 10);
    expect(drag.state.value).toMatchObject({ cardId: 7, x: 30, y: 10 });
    fire('pointerup', 30, 10);
    expect(dropped).toEqual([[7, 'done']]);
    expect(drag.state.value).toBeNull();
    stop();
  });

  it('the drop uses where the pointer is released, not the last move (no move in between, or the page scrolled)', () => {
    const { drag, dropped, stop } = setup({ hitTest: (x) => (x < 100 ? 'todo' : x < 200 ? 'done' : null) });
    drag.start(down('mouse', 10, 10), 7);
    fire('pointermove', 30, 10);
    expect(drag.state.value?.overKey).toBe('todo');
    fire('pointerup', 150, 10);
    expect(dropped).toEqual([[7, 'done']]);
    drag.start(down('mouse', 10, 10), 8);
    fire('pointermove', 30, 10);
    fire('pointerup', 300, 10);
    expect(dropped).toEqual([[7, 'done']]);
    stop();
  });

  it('start tells whether it took the press: a second pointer while tracking is refused', () => {
    const { drag, stop } = setup();
    expect(drag.start(down('mouse', 10, 10), 7)).toBe(true);
    expect(drag.start({ ...down('pen', 50, 50), pointerId: 2 } as PointerEvent, 8)).toBe(false);
    expect(drag.start({ ...down('mouse', 10, 10), isPrimary: false } as PointerEvent, 9)).toBe(false);
    stop();
  });

  it('a press that never moves past the threshold is a plain click and drops nothing', () => {
    const { drag, dropped, stop } = setup({ hitTest: () => 'done' });
    drag.start(down('mouse', 10, 10), 7);
    fire('pointermove', 11, 11);
    fire('pointerup', 11, 11);
    expect(dropped).toEqual([]);
    expect(drag.state.value).toBeNull();
    stop();
  });

  it('use-card-drag starts on touch only after long press', () => {
    const { drag, stop } = setup();
    drag.start(down('touch', 10, 10), 7);
    fire('pointermove', 12, 11, { pointerType: 'touch' });
    vi.advanceTimersByTime(200);
    expect(drag.state.value).toBeNull();
    vi.advanceTimersByTime(250);
    expect(drag.state.value).toMatchObject({ cardId: 7 });
    fire('pointerup', 12, 11, { pointerType: 'touch' });
    stop();
  });

  it('moving a finger far before the long press fires means scrolling, so no drag starts', () => {
    const { drag, stop } = setup();
    drag.start(down('touch', 10, 10), 7);
    fire('pointermove', 10, 60, { pointerType: 'touch' });
    vi.advanceTimersByTime(1000);
    expect(drag.state.value).toBeNull();
    stop();
  });

  it('use-card-drag is off on phone width', () => {
    const { drag, stop } = setup({ enabled: false });
    drag.start(down('mouse', 0, 0), 7);
    fire('pointermove', 50, 50);
    expect(drag.state.value).toBeNull();
    expect(dragModeOf(false, 'mouse')).toBe('off');
    expect(dragModeOf(true, 'touch')).toBe('longPress');
    expect(dragModeOf(true, 'pen')).toBe('mouse');
    expect(dragModeOf(true, 'mouse')).toBe('mouse');
    stop();
  });

  it('use-card-drag reports the column under the pointer via hitTest', () => {
    const hit = vi.fn((x: number) => (x < 100 ? 'todo' : x < 200 ? 'done' : null));
    const { drag, dropped, stop } = setup({ hitTest: hit });
    drag.start(down('mouse', 0, 0), 3);
    fire('pointermove', 20, 0);
    expect(drag.state.value?.overKey).toBe('todo');
    fire('pointermove', 150, 40);
    expect(hit).toHaveBeenLastCalledWith(150, 40);
    expect(drag.state.value?.overKey).toBe('done');
    fire('pointermove', 300, 40);
    expect(drag.state.value?.overKey).toBeNull();
    fire('pointerup', 300, 40);
    expect(dropped).toEqual([]);
    stop();
  });

  it('use-card-drag Esc cancels', () => {
    const { drag, dropped, stop } = setup({ hitTest: () => 'done' });
    drag.start(down('mouse', 0, 0), 3);
    fire('pointermove', 50, 0);
    expect(drag.state.value).not.toBeNull();
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(drag.state.value).toBeNull();
    fire('pointermove', 80, 0);
    fire('pointerup', 80, 0);
    expect(dropped).toEqual([]);
    stop();
  });

  it('swallows the click that follows a drag so the card does not open, and only that one click', () => {
    const { drag, stop } = setup({ hitTest: () => 'done' });
    const clicked = vi.fn();
    document.body.addEventListener('click', clicked);
    drag.start(down('mouse', 0, 0), 3);
    fire('pointermove', 50, 0);
    fire('pointerup', 50, 0);
    document.body.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    expect(clicked).not.toHaveBeenCalled();
    vi.advanceTimersByTime(10);
    document.body.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    expect(clicked).toHaveBeenCalledTimes(1);
    document.body.removeEventListener('click', clicked);
    stop();
  });

  it('ignores a non-primary mouse button and stops listening when its scope ends', () => {
    const hit = vi.fn(() => null);
    const { drag, stop } = setup({ hitTest: hit });
    drag.start({ ...down('mouse'), button: 2 } as PointerEvent, 1);
    fire('pointermove', 80, 0);
    expect(drag.state.value).toBeNull();
    drag.start(down('mouse'), 1);
    fire('pointermove', 40, 0);
    expect(drag.state.value).not.toBeNull();
    const calls = hit.mock.calls.length;
    stop();
    expect(drag.state.value).toBeNull();
    fire('pointermove', 80, 0);
    expect(hit.mock.calls.length).toBe(calls);
  });

  it('pointercancel (the browser took the touch for scrolling) ends the drag without dropping', () => {
    const { drag, dropped, stop } = setup({ hitTest: () => 'done' });
    drag.start(down('mouse', 0, 0), 3);
    fire('pointermove', 50, 0);
    expect(drag.state.value).not.toBeNull();
    fire('pointercancel', 50, 0);
    expect(drag.state.value).toBeNull();
    fire('pointerup', 50, 0);
    expect(dropped).toEqual([]);
    stop();
  });

  it('a second pointer cannot move, cancel or drop the card of the first one', () => {
    const { drag, dropped, stop } = setup({ hitTest: (x) => (x < 100 ? 'todo' : 'done') });
    drag.start(down('mouse', 0, 0), 3);
    fire('pointermove', 20, 0);
    fire('pointermove', 150, 0, { pointerId: 2, isPrimary: false });
    expect(drag.state.value).toMatchObject({ x: 20, overKey: 'todo' });
    fire('pointercancel', 150, 0, { pointerId: 2, isPrimary: false });
    fire('pointerup', 150, 0, { pointerId: 2, isPrimary: false });
    expect(drag.state.value).not.toBeNull();
    expect(dropped).toEqual([]);
    fire('pointerup', 20, 0);
    expect(dropped).toEqual([[3, 'todo']]);
    stop();
  });

  it('a second finger going down on another card neither starts a drag nor clears the shadow of the first', () => {
    const { drag, stop } = setup();
    drag.start(down('touch', 10, 10), 7);
    vi.advanceTimersByTime(450);
    expect(drag.state.value).toMatchObject({ cardId: 7 });
    drag.start({ ...down('touch', 200, 200), pointerId: 2, isPrimary: false } as PointerEvent, 9);
    expect(drag.state.value).toMatchObject({ cardId: 7 });
    fire('pointerup', 10, 10, { pointerType: 'touch' });
    expect(drag.state.value).toBeNull();
    // 沒有人在拖時，非主要指標也不會開始
    drag.start({ ...down('mouse', 0, 0), isPrimary: false } as PointerEvent, 9);
    fire('pointermove', 80, 0);
    expect(drag.state.value).toBeNull();
    stop();
  });

  it('a mouse move with no button pressed means the release was missed, so the drag is cancelled', () => {
    const { drag, dropped, stop } = setup({ hitTest: () => 'done' });
    drag.start(down('mouse', 0, 0), 3);
    fire('pointermove', 50, 0);
    expect(drag.state.value).not.toBeNull();
    fire('pointermove', 60, 0, { buttons: 0 });
    expect(drag.state.value).toBeNull();
    fire('pointerup', 60, 0, { buttons: 0 });
    expect(dropped).toEqual([]);
    stop();
  });

  it('losing window focus mid-drag (Alt+Tab) cancels the drag', () => {
    const { drag, dropped, stop } = setup({ hitTest: () => 'done' });
    drag.start(down('mouse', 0, 0), 3);
    fire('pointermove', 50, 0);
    window.dispatchEvent(new Event('blur'));
    expect(drag.state.value).toBeNull();
    fire('pointerup', 50, 0);
    expect(dropped).toEqual([]);
    stop();
  });

  describe('touch scrolling and the long-press menu', () => {
    const touchmove = () => new Event('touchmove', { bubbles: true, cancelable: true });

    it('the non-passive touchmove guard is registered up front on the board root, not at press time', () => {
      const root = document.createElement('div');
      const add = vi.spyOn(root, 'addEventListener');
      const { stop } = setup({ touchRoot: ref(root) });
      expect(add).toHaveBeenCalledWith('touchmove', expect.any(Function), { passive: false });
      stop();
    });

    it('touchmove is only cancelled while a long-press drag is on', () => {
      const root = document.createElement('div');
      const inner = root.appendChild(document.createElement('div'));
      const { drag, stop } = setup({ touchRoot: ref(root) });
      const before = touchmove();
      inner.dispatchEvent(before);
      expect(before.defaultPrevented).toBe(false);
      drag.start(down('touch', 10, 10), 7);
      const waiting = touchmove();
      inner.dispatchEvent(waiting);
      expect(waiting.defaultPrevented).toBe(false);
      vi.advanceTimersByTime(450);
      const dragging = touchmove();
      inner.dispatchEvent(dragging);
      expect(dragging.defaultPrevented).toBe(true);
      fire('pointerup', 10, 10, { pointerType: 'touch' });
      const after = touchmove();
      inner.dispatchEvent(after);
      expect(after.defaultPrevented).toBe(false);
      stop();
    });

    it('the long-press context menu is swallowed while pressing and given back afterwards', () => {
      const { drag, stop } = setup();
      drag.start(down('touch', 10, 10), 7);
      const during = new Event('contextmenu', { cancelable: true });
      window.dispatchEvent(during);
      expect(during.defaultPrevented).toBe(true);
      vi.advanceTimersByTime(450);
      fire('pointerup', 10, 10, { pointerType: 'touch' });
      const after = new Event('contextmenu', { cancelable: true });
      window.dispatchEvent(after);
      expect(after.defaultPrevented).toBe(false);
      stop();
    });
  });
});
