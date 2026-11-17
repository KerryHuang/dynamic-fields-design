import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { effectScope } from 'vue';
import { dragModeOf, useCardDrag, type DragOptions } from './use-card-drag';

const fire = (type: string, x = 0, y = 0, extra: Record<string, unknown> = {}) => {
  const e = new MouseEvent(type, { clientX: x, clientY: y, bubbles: true, cancelable: true });
  Object.assign(e, { pointerType: 'mouse', pointerId: 1 }, extra);
  window.dispatchEvent(e);
  return e;
};
const down = (pointerType: string, x = 0, y = 0) => ({ pointerType, pointerId: 1, button: 0, clientX: x, clientY: y, target: document.body } as unknown as PointerEvent);

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
    let on = false;
    const { drag, stop } = setup({ enabled: () => on });
    drag.start(down('mouse', 0, 0), 7);
    fire('pointermove', 50, 50);
    expect(drag.state.value).toBeNull();
    on = true;
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
    const { drag, stop } = setup();
    drag.start({ ...down('mouse'), button: 2 } as PointerEvent, 1);
    fire('pointermove', 80, 0);
    expect(drag.state.value).toBeNull();
    drag.start(down('mouse'), 1);
    stop();
    fire('pointermove', 80, 0);
    expect(drag.state.value).toBeNull();
  });
});
