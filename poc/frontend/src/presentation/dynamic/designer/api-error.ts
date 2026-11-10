import type { ApiError } from 'src/services/api/http';
import { joinList } from 'src/i18n/translate';

/** Server's RowVersion-conflict message; any other 409 message (e.g. cascade depth) is shown as is. */
export const SERVER_STALE_MESSAGE = '資料已被其他人修改，請重新載入';

/** Field-level messages whose backend key starts with `prefix` (e.g. 'Config.MaxLength'), else undefined. */
export function errorFor(e: ApiError | null, prefix: string): string | undefined {
  const hit = Object.entries(e?.errors ?? {}).filter(([k]) => k.startsWith(prefix)).flatMap(([, v]) => v);
  return hit.length ? joinList(hit) : undefined;
}

/** Notify only what no input shows; a generic message when the error carries nothing at all. */
export function notifyApiError(
  notify: (o: { type: string; message: string }) => void, e: ApiError, shown: string[], fallback: string,
) {
  const rest = leftoverMessages(e, shown);
  if (rest.length) notify({ type: 'negative', message: joinList(rest) });
  else if (!Object.keys(e.errors ?? {}).length) notify({ type: 'negative', message: fallback });
}

/** Everything that did not land on an input, for Notify. */
export function leftoverMessages(e: ApiError, shown: string[]): string[] {
  const rest = Object.entries(e.errors ?? {}).filter(([k]) => !shown.some((p) => k.startsWith(p))).flatMap(([, v]) => v);
  if (e.message) rest.push(e.message);
  return rest;
}

/** Error messages whose key is not exactly one of `keys` (unlike leftoverMessages, no prefix matching: f_1 vs f_10). */
export function messagesOutside(errors: Record<string, string[]> | undefined, keys: string[]): string[] {
  return Object.entries(errors ?? {}).filter(([k]) => !keys.includes(k)).flatMap(([, v]) => v);
}
