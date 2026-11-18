import { afterAll } from 'vitest';

// Quasar 元件（QSelect 的 filter／focus、QMenu、QTooltip、Notify）會排 setTimeout，
// 測試檔結束、jsdom 拆除後才觸發會讀到 window，讓 vitest 回報 Unhandled Error
// 「ReferenceError: window is not defined」而 exit 1（CI 偶發）。
// 這裡記下每個還沒觸發的計時器，檔案結束前（jsdom 拆除前）一律清掉。
// 用 fake timers 的測試不受影響（fake timers 會替換掉這層包裝）。
const realSetTimeout = globalThis.setTimeout;
const realClearTimeout = globalThis.clearTimeout;
const pending = new Set<number>();

globalThis.setTimeout = ((handler: TimerHandler, timeout?: number, ...args: unknown[]) => {
  const id = realSetTimeout(((...a: unknown[]) => {
    pending.delete(id);
    return typeof handler === 'function' ? (handler as (...x: unknown[]) => unknown)(...a) : undefined;
  }) as TimerHandler, timeout, ...args);
  pending.add(id);
  return id;
}) as unknown as typeof setTimeout;

globalThis.clearTimeout = ((id?: number) => {
  if (id !== undefined) pending.delete(id);
  realClearTimeout(id);
}) as unknown as typeof clearTimeout;

function flushPendingTimers() {
  for (const id of pending) realClearTimeout(id);
  pending.clear();
}

afterAll(flushPendingTimers);
