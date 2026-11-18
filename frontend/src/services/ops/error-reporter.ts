import { currentLocale } from 'src/i18n/translate';
import { clientVersion } from './build-info';
import { recentFailedRequestId } from './last-failure';

/** 前端錯誤回報（設計 docs/superpowers/specs/2026-11-11-ops-observability-design.md §10.3）。 */
export type ClientErrorKind = 'vue' | 'window' | 'promise' | 'chunk';

/**
 * 技術限制（設計 §12）：訊息 500 字、stack 4000 字（與後端截斷一致）、同指紋 5 分鐘一次、每分頁每分鐘 10 則；
 * maxBytes 是序列化後的 UTF-8 位元組上限，比後端 8KB 留 512 位元組餘裕（O1 計畫 Task 16）。
 */
export const LIMITS = { messageMax: 500, stackMax: 4000, dedupeMs: 300_000, perMinute: 10, maxBytes: 7680 } as const;

/** 其他欄位上限，與後端 ClientErrorLimits 相同（O1 計畫判斷 J13）。 */
export const FIELD_MAX = { name: 100, moduleKey: 100, route: 200, browser: 200, version: 50, locale: 20 } as const;

/** 瀏覽器雜訊：訊息以這些字開頭就不回報。技術限制（O1 計畫判斷 J24）。 */
export const IGNORED_MESSAGES = ['ResizeObserver loop'] as const;

export const CLIENT_ERRORS_URL = '/api/v3/dynamic/ops/client-errors';

export interface ClientErrorReport {
  kind: ClientErrorKind;
  name: string;
  message: string;
  stack: string;
  route: string | null;
  moduleKey: string | null;
  version: string;
  locale: string;
  browser: string;
  occurredAt: string;
  lastRequestId?: string;
}

export interface RouteLike {
  matched: { path: string }[];
  params: Record<string, unknown>;
}

function parts(error: unknown): { name: string; message: string; stack: string } {
  if (error instanceof Error) return { name: error.name, message: error.message, stack: error.stack ?? '' };
  if (typeof error === 'string') return { name: '', message: error, stack: '' };
  // 不序列化內容：rejection 物件可能帶權杖、記錄 id、輸入值或完整網址（審查修正）
  const kind = typeof error === 'object' && error !== null ? (Object.getPrototypeOf(error)?.constructor?.name || 'Object') : typeof error;
  return { name: '', message: `非 Error 物件：${kind}`, stack: '' };
}

/** 瀏覽器家族加主版本，例如 Chrome 131；不送完整 User-Agent。 */
export function browserFamily(ua: string = typeof navigator === 'undefined' ? '' : navigator.userAgent): string {
  const rules: [string, RegExp][] = [
    ['Edge', /Edg(?:e|A|iOS)?\/(\d+)/], ['Opera', /OPR\/(\d+)/], ['Samsung Internet', /SamsungBrowser\/(\d+)/],
    ['Firefox', /(?:Firefox|FxiOS)\/(\d+)/], ['Chrome', /(?:Chrome|CriOS)\/(\d+)/], ['Safari', /Version\/(\d+)[^ ]* (?:Mobile\/\S+ )?Safari\//],
  ];
  for (const [name, re] of rules) {
    const m = re.exec(ua);
    if (m) return `${name} ${m[1]}`;
  }
  return 'Other';
}

const encoder = new TextEncoder();
const bytes = (r: ClientErrorReport) => encoder.encode(JSON.stringify(r)).length;

const cut = (v: string, max: number) => v.slice(0, max);

export function buildReport(kind: ClientErrorKind, error: unknown, route: RouteLike | null | undefined, now: number): ClientErrorReport {
  const p = parts(error);
  const moduleKey = route?.params.moduleKey;
  const path = route?.matched.at(-1)?.path;
  const lastRequestId = recentFailedRequestId(now);
  const report: ClientErrorReport = {
    kind,
    name: cut(p.name, FIELD_MAX.name),
    message: p.message.slice(0, LIMITS.messageMax),
    stack: p.stack.slice(0, LIMITS.stackMax),
    // 只送路由樣板（/dynamic/:moduleKey/:id），不送 fullPath：不含記錄 id 與查詢字串
    route: path === undefined ? null : cut(path, FIELD_MAX.route),
    moduleKey: typeof moduleKey === 'string' ? cut(moduleKey, FIELD_MAX.moduleKey) : null,
    version: cut(clientVersion(), FIELD_MAX.version),
    locale: cut(currentLocale(), FIELD_MAX.locale),
    browser: cut(browserFamily(), FIELD_MAX.browser),
    occurredAt: new Date(now).toISOString(),
    ...(lastRequestId ? { lastRequestId } : {}),
  };
  // 其他欄位已有上限；中文一字 3 位元組、控制字元跳脫後 6 位元組：超過上限先砍 stack，砍完仍超過再縮訊息
  while (bytes(report) > LIMITS.maxBytes && report.stack) report.stack = report.stack.slice(0, Math.floor(report.stack.length / 2));
  while (bytes(report) > LIMITS.maxBytes && report.message) report.message = report.message.slice(0, Math.floor(report.message.length / 2));
  return report;
}

const CHUNK_MESSAGES = ['Failed to fetch dynamically imported module', 'Importing a module script failed', 'error loading dynamically imported module', 'Unable to preload CSS for'];

/** 動態載入的程式塊載入失敗（部署新版後舊頁面常見）。 */
export function isChunkLoadError(error: unknown): boolean {
  const { name, message } = parts(error);
  return name === 'ChunkLoadError' || CHUNK_MESSAGES.some((m) => message.includes(m));
}

/** http.ts 的 ApiError：API 失敗後端已有 log，不當前端錯誤回報（O1 計畫判斷 J24）。 */
export function isApiError(reason: unknown): boolean {
  return typeof reason === 'object' && reason !== null && !(reason instanceof Error) && typeof (reason as { status?: unknown }).status === 'number';
}

export function shouldReport(kind: ClientErrorKind, error: unknown): boolean {
  if ((kind === 'vue' || kind === 'promise') && isApiError(error)) return false;
  const { message } = parts(error);
  return !IGNORED_MESSAGES.some((m) => message.startsWith(m));
}

/** 不經 axios（不走攔截器、不記失敗 request id）；任何失敗都丟掉，不重試、不再回報。 */
export async function sendReport(r: ClientErrorReport): Promise<void> {
  try {
    await fetch(CLIENT_ERRORS_URL, {
      method: 'POST', keepalive: true, credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json', 'X-Locale': currentLocale() },
      body: JSON.stringify(r),
    });
  } catch { /* 丟掉 */ }
}

/** 與後端 ErrorFingerprint.NormalizeMessage 相同的正規化（設計 §10.2.6）：只差數字、id 的同一錯誤算同一個。 */
export function normalizeMessage(message: string): string {
  return message
    .replace(/'[^']*'|"[^"]*"/g, '{s}')
    .replace(/[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/g, '{email}')
    .replace(/\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b/g, '{guid}')
    .replace(/\b(?=[0-9a-fA-F]*[a-fA-F])[0-9a-fA-F]{8,}\b/g, '{hex}')
    .replace(/\d+/g, '{n}')
    .slice(0, 200);
}

const CHROME_FRAME = /^at (?:(.+?) \()?(.+?):\d+:\d+\)?$/;
const FIREFOX_FRAME = /^([^@\s]*)@(.+?):\d+:\d+$/;

/** 與後端 ErrorFingerprint.ClientFrames 相同：最上面三個框的「函式名@去掉雜湊的檔名」，不含行列號。 */
export function clientFrames(stack: string): string[] {
  const frames: string[] = [];
  for (const raw of stack.split('\n')) {
    const line = raw.trim();
    const m = CHROME_FRAME.exec(line) ?? FIREFOX_FRAME.exec(line);
    if (!m) continue;
    const file = (m[2].split('?')[0] ?? '').split('/').pop()!.replace(/-[A-Za-z0-9_-]{8}(?=\.js$)/, '');
    frames.push(`${m[1] || '?'}@${file}`);
    if (frames.length === 3) break;
  }
  return frames;
}

/** 本機去重鍵：與後端前端錯誤指紋同組成（種類、名稱、正規化訊息、三個呼叫框、路由樣板）。 */
const dedupeKey = (r: ClientErrorReport) =>
  [r.kind, r.name, normalizeMessage(r.message), clientFrames(r.stack).join('|'), r.route ?? ''].join('\n');

export function createReporter(deps: { send: (r: ClientErrorReport) => Promise<void>; now: () => number }) {
  const lastSent = new Map<string, number>();
  // 同一個錯誤物件可能從兩個入口進來（vite:preloadError 之後 Vite 再丟一次給 router／unhandledrejection），只算一次
  const seen = new WeakSet<object>();
  let recent: number[] = [];
  return {
    report(kind: ClientErrorKind, error: unknown, route?: RouteLike | null): void {
      try {
        if (!shouldReport(kind, error)) return;
        if (typeof error === 'object' && error !== null) {
          if (seen.has(error)) return;
          seen.add(error);
        }
        const now = deps.now();
        const r = buildReport(kind, error, route, now);
        const key = dedupeKey(r);
        const prev = lastSent.get(key);
        if (prev !== undefined && now - prev < LIMITS.dedupeMs) return;
        recent = recent.filter((t) => now - t < 60_000);
        if (recent.length >= LIMITS.perMinute) return;
        for (const [k, t] of lastSent) if (now - t >= LIMITS.dedupeMs) lastSent.delete(k);
        lastSent.set(key, now);
        recent.push(now);
        deps.send(r).catch(() => { /* 丟掉 */ });
      } catch { /* 回報本身出錯也不再回報，避免迴圈 */ }
    },
  };
}
