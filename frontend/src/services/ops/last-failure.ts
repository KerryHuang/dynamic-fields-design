/** 最近一次失敗 API 的 request id 只在這段時間內附進前端錯誤回報。技術限制（設計 §12）。 */
export const RECENT_MS = 30_000;

let last: { id: string; at: number } | undefined;

export function recordFailedRequest(id: string, now = Date.now()): void {
  last = { id, at: now };
}

export function recentFailedRequestId(now = Date.now()): string | undefined {
  return last && now - last.at <= RECENT_MS ? last.id : undefined;
}
