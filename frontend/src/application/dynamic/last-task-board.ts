const storageKey = (userId: string) => `taskBoard.last.${userId}`;

/** 這個使用者上次打開的看板 id；沒打開過、儲存空間被封鎖或內容壞掉時是 null（走預設看板）。 */
export function readLastTaskBoard(userId: string): number | null {
  if (!userId) return null;
  try {
    const v: unknown = JSON.parse(localStorage.getItem(storageKey(userId)) ?? 'null');
    return typeof v === 'number' && Number.isInteger(v) && v > 0 ? v : null;
  } catch {
    return null;
  }
}

export function writeLastTaskBoard(userId: string, id: number) {
  if (!userId) return;
  try {
    localStorage.setItem(storageKey(userId), JSON.stringify(id));
  } catch {
    // 無痕或被封鎖時略過：只是便利功能
  }
}

/** 看板被刪、不再分享，或使用者改選預設看板時清掉記憶（D6）。 */
export function clearLastTaskBoard(userId: string) {
  if (!userId) return;
  try {
    localStorage.removeItem(storageKey(userId));
  } catch {
    // 同上
  }
}
