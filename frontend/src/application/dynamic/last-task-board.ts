import type { TaskViewKind } from 'src/domain/dynamic/task-board-types';

// 看板與甘特圖各記各的（看板設計 §11.5.4.1）：taskBoard.last.<使用者>、taskGantt.last.<使用者>
const storageKey = (userId: string, kind: TaskViewKind) => `${kind === 'gantt' ? 'taskGantt' : 'taskBoard'}.last.${userId}`;

/** 這個使用者上次打開的看板（或甘特圖）id；沒打開過、儲存空間被封鎖或內容壞掉時是 null（走預設看板／預設甘特圖）。 */
export function readLastTaskBoard(userId: string, kind: TaskViewKind = 'board'): number | null {
  if (!userId) return null;
  try {
    const v: unknown = JSON.parse(localStorage.getItem(storageKey(userId, kind)) ?? 'null');
    return typeof v === 'number' && Number.isInteger(v) && v > 0 ? v : null;
  } catch {
    return null;
  }
}

export function writeLastTaskBoard(userId: string, id: number, kind: TaskViewKind = 'board') {
  if (!userId) return;
  try {
    localStorage.setItem(storageKey(userId, kind), JSON.stringify(id));
  } catch {
    // 無痕或被封鎖時略過：只是便利功能
  }
}

/** 看板（或甘特圖）被刪、不再分享，或使用者改選預設時清掉記憶（D6）。 */
export function clearLastTaskBoard(userId: string, kind: TaskViewKind = 'board') {
  if (!userId) return;
  try {
    localStorage.removeItem(storageKey(userId, kind));
  } catch {
    // 同上
  }
}
