import { afterEach, describe, expect, it, vi } from 'vitest';
import { clearLastTaskBoard, readLastTaskBoard, writeLastTaskBoard } from './last-task-board';

afterEach(() => { localStorage.clear(); vi.restoreAllMocks(); });

describe('last-task-board', () => {
  it('last-task-board is stored per user and ignores broken content', () => {
    writeLastTaskBoard('a', 3);
    writeLastTaskBoard('b', 5);
    expect(readLastTaskBoard('a')).toBe(3);
    expect(readLastTaskBoard('b')).toBe(5);
    expect(readLastTaskBoard('c')).toBeNull();
    localStorage.setItem('taskBoard.last.a', '{oops');
    expect(readLastTaskBoard('a')).toBeNull();
    localStorage.setItem('taskBoard.last.a', '"3"');
    expect(readLastTaskBoard('a')).toBeNull();
    localStorage.setItem('taskBoard.last.a', '0');
    expect(readLastTaskBoard('a')).toBeNull();
    expect(readLastTaskBoard('')).toBeNull();
  });

  it('clear forgets only that user', () => {
    writeLastTaskBoard('a', 3);
    writeLastTaskBoard('b', 5);
    clearLastTaskBoard('a');
    expect(readLastTaskBoard('a')).toBeNull();
    expect(readLastTaskBoard('b')).toBe(5);
  });

  it('never throws when storage is blocked', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('blocked'); });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('blocked'); });
    vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => { throw new Error('blocked'); });
    expect(readLastTaskBoard('a')).toBeNull();
    expect(() => writeLastTaskBoard('a', 1)).not.toThrow();
    expect(() => clearLastTaskBoard('a')).not.toThrow();
  });
});
