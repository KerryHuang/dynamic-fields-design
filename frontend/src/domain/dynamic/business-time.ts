// 營運時區（政策 locale.timeZone）：固定 UTC 偏移、沒有日光節約。值由登入後的第一次導覽從 API 載入（session-settings.ts）。
// 只能在登入後的頁面呼叫，未設定就丟錯；公開頁面（登入、服務暫停）不可以用。

const OFFSET = /^([+-])(\d{2}):(\d{2})$/;
const WITH_OFFSET = /^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2})(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})$/;
const WALL = /^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2})(:\d{2})?(\.\d+)?$/;

let current: { text: string; ms: number } | null = null;

export function setBusinessOffset(offset: string): void {
  const m = OFFSET.exec(offset);
  if (!m) throw new Error(`invalid UTC offset: ${offset}`);
  const ms = (Number(m[2]) * 60 + Number(m[3])) * 60_000;
  current = { text: offset, ms: m[1] === '-' ? -ms : ms };
}

export function isBusinessOffsetLoaded(): boolean {
  return current !== null;
}

function loaded(): { text: string; ms: number } {
  if (!current) throw new Error('business time zone not loaded');
  return current;
}

/** 目前的營運時區偏移，例如出廠值 UTC+8（`+HH:mm` 格式）。 */
export function businessOffset(): string {
  return loaded().text;
}

export function businessOffsetMs(): number {
  return loaded().ms;
}

/** 帶 Z 或任何偏移 → 營運時區的 `yyyy-MM-ddTHH:mm:ss`；不帶偏移視為已是營運時區（補秒）；解析不了回 null。 */
export function toBusinessWallClock(iso: string): string | null {
  const wall = WALL.exec(iso);
  if (wall) return `${wall[1]}${wall[2] ?? ':00'}`;
  if (!WITH_OFFSET.test(iso)) return null;
  const ms = Date.parse(iso);
  if (Number.isNaN(ms)) return null;
  return new Date(ms + businessOffsetMs()).toISOString().slice(0, 19);
}

/** 營運時區的牆上時間（`yyyy-MM-ddTHH:mm` 或含秒）補上營運偏移。 */
export function withBusinessOffset(wall: string): string {
  const s = wall.length === 16 ? `${wall}:00` : wall.slice(0, 19);
  return `${s}${businessOffset()}`;
}

/** 營運時區的今天（`yyyy-MM-dd`），不看瀏覽器時區。 */
export function businessToday(now: Date = new Date()): string {
  return new Date(now.getTime() + businessOffsetMs()).toISOString().slice(0, 10);
}
