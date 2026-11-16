const storageKey = (userId: string) => `dashboard.last.${userId}`;

/** 這個使用者上次看的儀表板 id；儲存空間被封鎖或內容壞掉時就是沒有紀錄。 */
export function readLastDashboard(userId: string): number | null {
  if (!userId) return null;
  try {
    const v: unknown = JSON.parse(localStorage.getItem(storageKey(userId)) ?? 'null');
    return typeof v === 'number' && Number.isInteger(v) && v > 0 ? v : null;
  } catch {
    return null;
  }
}

export function writeLastDashboard(userId: string, id: number) {
  if (!userId) return;
  try {
    localStorage.setItem(storageKey(userId), JSON.stringify(id));
  } catch {
    // 無痕或被封鎖時略過：只是便利功能，換裝置本來就開第一張
  }
}

/** 首頁與 /dashboards 要開哪一張：上次看的還在清單裡就開它，否則第一張（指派版在前，依管理員順序）；沒有任何儀表板回 null。 */
export function pickDashboard(ids: number[], last: number | null): number | null {
  if (last !== null && ids.includes(last)) return last;
  return ids[0] ?? null;
}
