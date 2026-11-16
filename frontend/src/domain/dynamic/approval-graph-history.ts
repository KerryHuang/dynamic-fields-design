// 設計頁復原／重做堆疊（U60）：不可變，整份快照（圖上限 100 節點、200 線，不做差分）。
export interface History<T> {
  past: T[];
  present: T;
  future: T[];
  limit: number;
}

export const createHistory = <T>(initial: T, limit = 50): History<T> => ({ past: [], present: initial, future: [], limit });

/** 記一步；值相同（同一個參照）不算一步；新的修改丟掉重做分支，過去最多留 limit 步。 */
export function push<T>(h: History<T>, next: T): History<T> {
  if (Object.is(next, h.present)) return h;
  return { ...h, past: [...h.past, h.present].slice(-h.limit), present: next, future: [] };
}

export const canUndo = <T>(h: History<T>) => h.past.length > 0;
export const canRedo = <T>(h: History<T>) => h.future.length > 0;

export function undo<T>(h: History<T>): History<T> {
  if (!canUndo(h)) return h;
  return { ...h, past: h.past.slice(0, -1), present: h.past[h.past.length - 1], future: [h.present, ...h.future] };
}

export function redo<T>(h: History<T>): History<T> {
  if (!canRedo(h)) return h;
  return { ...h, past: [...h.past, h.present], present: h.future[0], future: h.future.slice(1) };
}

/** 發布或捨棄草稿後清空（U60）。 */
export const clear = <T>(h: History<T>): History<T> => ({ ...h, past: [], future: [] });
