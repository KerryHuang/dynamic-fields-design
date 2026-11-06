/** 多存一些：被停用或收回權限的模組在首頁會被濾掉，不該讓清單縮水。首頁只顯示前 5 個。 */
export const RECENT_STORED = 10;
export const RECENT_SHOWN = 5;
const storageKey = (userId: string) => `formwright:recent-modules:${userId}`;

/** Module keys the user opened lately, newest first. Storage may be blocked or corrupt: then there is simply no history. */
export function readRecent(userId: string): string[] {
  if (!userId) return [];
  try {
    const v: unknown = JSON.parse(localStorage.getItem(storageKey(userId)) ?? '[]');
    return Array.isArray(v) ? v.filter((k): k is string => typeof k === 'string').slice(0, RECENT_STORED) : [];
  } catch {
    return [];
  }
}

export function recordRecent(userId: string, moduleKey: string) {
  if (!userId || !moduleKey) return;
  try {
    const next = [moduleKey, ...readRecent(userId).filter((k) => k !== moduleKey)].slice(0, RECENT_STORED);
    localStorage.setItem(storageKey(userId), JSON.stringify(next));
  } catch {
    // 無痕或被封鎖時略過：最近開過只是便利功能
  }
}
