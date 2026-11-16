import { describe, expect, it } from 'vitest';
import { canRedo, canUndo, clear, createHistory, push, redo, undo } from './approval-graph-history';

describe('approval graph history', () => {
  it('undo and redo walk back and forth', () => {
    let h = createHistory('a');
    h = push(h, 'b');
    h = push(h, 'c');
    expect(h.present).toBe('c');
    h = undo(h);
    expect(h.present).toBe('b');
    h = undo(h);
    expect(h.present).toBe('a');
    expect(canUndo(h)).toBe(false);
    h = redo(h);
    h = redo(h);
    expect(h.present).toBe('c');
    expect(canRedo(h)).toBe(false);
  });

  it('undo and redo at the ends return the same history', () => {
    const h = createHistory('a');
    expect(undo(h)).toBe(h);
    expect(redo(h)).toBe(h);
  });

  it('a new edit after undo drops the redo branch', () => {
    let h = push(push(createHistory(1), 2), 3);
    h = undo(undo(h));
    expect(canRedo(h)).toBe(true);
    h = push(h, 9);
    expect(canRedo(h)).toBe(false);
    expect(undo(h).present).toBe(1);
  });

  it('history keeps at most 50 steps', () => {
    let h = createHistory(0);
    for (let i = 1; i <= 60; i++) h = push(h, i);
    let steps = 0;
    while (canUndo(h)) { h = undo(h); steps++; }
    expect(steps).toBe(50);
    expect(h.present).toBe(10);
  });

  it('the limit can be changed', () => {
    let h = createHistory(0, 2);
    for (let i = 1; i <= 5; i++) h = push(h, i);
    expect(undo(undo(h)).present).toBe(3);
    expect(canUndo(undo(undo(h)))).toBe(false);
  });

  it('pushing the same value again is not a step', () => {
    const h = createHistory('a');
    expect(push(h, 'a')).toBe(h);
  });

  it('clear keeps the present and forgets both directions', () => {
    let h = push(push(createHistory(1), 2), 3);
    h = undo(h);
    h = clear(h);
    expect(h.present).toBe(2);
    expect(canUndo(h)).toBe(false);
    expect(canRedo(h)).toBe(false);
  });
});
