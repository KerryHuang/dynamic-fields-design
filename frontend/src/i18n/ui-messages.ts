import { baseCompile } from '@intlify/message-compiler';
import { Notify } from 'quasar';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { useIdentityStore } from 'src/stores/use-identity-store';
import { FALLBACK_LOCALE, STARTUP_BUDGET_MS, applyLocale, readLocale, type LocaleCode } from './locale';
import { defaultLocaleOrNull, localeInfo, nativeName, type LocaleInfo } from './locale-settings';
import { previewEpoch, previewLocale, setPreviewLocale } from './locale-preview';

// 介面文字執行期載入（語系包設計 §8.2–8.4、i4b 計畫判斷 11、14–16）。前端只打包繁中；其他語系（含繁中的覆寫）從
// GET i18n/ui/{code} 取得扁平文字，預編譯後轉巢狀再 setLocaleMessage。i18n 實例一律當參數（測試用乾淨實例），
// 元件與頁面傳 src/i18n/instance 的單例（LocaleSwitcher 傳 useI18n 的 global composer）。

export type I18nLike = {
  global: { setLocaleMessage(code: string, m: object): void; readonly availableLocales: string[]; locale: { value: string } };
};
export type UiMessages = { hash: string; messages: Record<string, string> };

/** 文字請求逾時（axios 實例沒有全域逾時，永不回的請求會讓「待重試」永遠不發生）；技術限制，列在設計 §14。 */
export const UI_FETCH_TIMEOUT_MS = 15000;
/** 瀏覽器快取只存一份（目前實際採用的非試看語系）。 */
export const UI_CACHE_KEY = 'formwright.ui';

const flatten = (o: unknown, path = '', out: Record<string, string> = {}): Record<string, string> => {
  if (typeof o === 'string') out[path] = o;
  else if (o && typeof o === 'object') for (const [k, v] of Object.entries(o)) flatten(v, path ? `${path}.${k}` : k, out);
  return out;
};
/** 打包的繁中（基準鍵集）。 */
const BASE = flatten(zhTW);

const compiles = (msg: string): boolean => {
  let ok = true;
  try {
    baseCompile(msg, { onError: () => { ok = false; } });
  } catch {
    ok = false;
  }
  return ok;
};

/**
 * 伺服器回的扁平文字 → 巢狀：只收基準鍵集內的字串；編譯不過的句子（縱深防禦，白名單驗證在後端匯入時）改用繁中同鍵。
 * dropped：丟掉的鍵（壞句、非字串、基準外）；missing：伺服器沒給的基準鍵（補繁中）。
 */
export function prepareMessages(flat: Record<string, unknown>, base: Record<string, string>): { nested: object; dropped: string[]; missing: string[] } {
  const nested: Record<string, unknown> = {};
  const dropped: string[] = [];
  const missing: string[] = [];
  for (const k of Object.keys(flat)) if (!(k in base)) dropped.push(k);
  for (const [k, fallback] of Object.entries(base)) {
    const v = flat[k];
    let msg = fallback;
    if (v === undefined) missing.push(k);
    else if (typeof v !== 'string' || !compiles(v)) dropped.push(k);
    else msg = v;
    const parts = k.split('.');
    let node = nested;
    for (const p of parts.slice(0, -1)) node = (node[p] ??= {}) as Record<string, unknown>;
    node[parts[parts.length - 1]!] = msg;
  }
  return { nested, dropped, missing };
}

interface Slot {
  /** 已裝上的雜湊 */
  loaded?: string;
  /** 伺服器最新給的雜湊 */
  wanted?: string;
  /** 最後一次發出的請求序號；完成時不是最新的就作廢 */
  seq: number;
  inflight?: { target: string | undefined; promise: Promise<void> };
  /** 已裝上的扁平文字（寫瀏覽器快取用） */
  flat?: Record<string, string>;
}
const slots = new Map<LocaleCode, Slot>();
const slot = (code: LocaleCode): Slot => {
  let s = slots.get(code);
  if (!s) slots.set(code, (s = { seq: 0 }));
  return s;
};

export function resetUiMessagesForTests(): void {
  slots.clear();
  switchSeq = 0;
}

export function installUiMessages(i18n: I18nLike, code: LocaleCode, flat: Record<string, unknown>, hash: string): void {
  const { nested, dropped, missing } = prepareMessages(flat, BASE);
  if (import.meta.env.DEV) {
    if (missing.length) console.warn(`[i18n] ${code} 缺 ${missing.length} 個鍵，改用繁中：`, missing);
    if (dropped.length) console.warn(`[i18n] ${code} 丟掉 ${dropped.length} 句（無法編譯或不在基準鍵集）：`, dropped);
  }
  i18n.global.setLocaleMessage(code, nested);
  const s = slot(code);
  s.loaded = hash;
  s.flat = Object.fromEntries(Object.entries(flat).filter((e): e is [string, string] => typeof e[1] === 'string'));
}

/** 每次呼叫都讀 i18n 目前的語系清單（不留快照）；打包的繁中一定有。 */
export const hasUiMessages = (i18n: I18nLike, code: LocaleCode): boolean => i18n.global.availableLocales.includes(code);
export const loadedHash = (code: LocaleCode): string | undefined => slots.get(code)?.loaded;

export function loadUiMessages(code: LocaleCode, hash?: string): Promise<UiMessages> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  const timeout = new Promise<never>((_, reject) => { timer = setTimeout(() => reject(new Error('ui messages timeout')), UI_FETCH_TIMEOUT_MS); });
  let req: Promise<UiMessages>;
  try {
    req = dynamicApi.getUiMessages(code, hash);
  } catch (e) {
    req = Promise.reject(e instanceof Error ? e : new Error(String(e)));
  }
  return Promise.race([req, timeout]).finally(() => clearTimeout(timer));
}

/** 語系資訊給的目標雜湊；空字串（內建清單、伺服器連不上）＝不知道。 */
const targetOf = (code: LocaleCode): string | undefined => localeInfo(code)?.uiHash || undefined;
/** 已有文字且符合目標（目標不知道時有文字就算）。 */
const upToDate = (i18n: I18nLike, code: LocaleCode, target: string | undefined) =>
  hasUiMessages(i18n, code) && (target === undefined || slots.get(code)?.loaded === target);

/** 把目前語系已裝上的文字寫進瀏覽器快取（試看中的語系、沒取過伺服器文字就不寫）。 */
export function cacheCurrent(i18n: I18nLike): void {
  const code = i18n.global.locale.value;
  const s = slots.get(code);
  if (s?.loaded !== undefined && s.flat && previewLocale() !== code) writeUiCache(code, s.loaded, s.flat);
}

/**
 * 版本協調（判斷 15）：只合併「同語系、同目標雜湊」的請求；目標改了就發新請求，舊的作廢。完成時序號不是最新的就丟掉；
 * 回應雜湊等於目標才裝上（還沒有任何文字的語系例外，先裝上）；不等或失敗維持待重試。失敗時 reject。
 */
function request(i18n: I18nLike, code: LocaleCode, target: string | undefined): Promise<void> {
  const s = slot(code);
  if (target !== undefined) s.wanted = target;
  if (s.inflight && s.inflight.target === target) return s.inflight.promise;
  const seq = ++s.seq;
  const promise = loadUiMessages(code, target).then((r) => {
    if (seq !== s.seq) return;
    if (s.wanted !== undefined && r.hash !== s.wanted && hasUiMessages(i18n, code)) return;
    installUiMessages(i18n, code, r.messages, r.hash);
    if (code === i18n.global.locale.value) cacheCurrent(i18n);
  }).finally(() => {
    if (s.inflight?.promise === promise) s.inflight = undefined;
  });
  s.inflight = { target, promise };
  return promise;
}

/**
 * 一次產生多語的地方用（判斷 16）：把 codes 都載入，最晚在 budgetMs 時 resolve，回傳現在有文字的語系（含打包的繁中）；
 * 沒回來的在背景繼續，單一語系失敗不丟例外。
 */
export async function ensureLocaleMessages(i18n: I18nLike, codes: LocaleCode[], opts?: { budgetMs?: number }): Promise<LocaleCode[]> {
  const pending = codes.filter((c) => !upToDate(i18n, c, targetOf(c))).map((c) => request(i18n, c, targetOf(c)).catch(() => undefined));
  if (pending.length) {
    let timer: ReturnType<typeof setTimeout> | undefined;
    const budget = new Promise<void>((resolve) => { timer = setTimeout(resolve, opts?.budgetMs ?? STARTUP_BUDGET_MS); });
    await Promise.race([Promise.all(pending), budget]);
    clearTimeout(timer);
  }
  return codes.filter((c) => hasUiMessages(i18n, c));
}

/** 切換語系用：已有符合目標的文字就直接回；否則取一次，取完仍沒有文字就丟錯（有舊版文字時照用）。 */
export async function ensureCurrentMessages(i18n: I18nLike, code: LocaleCode): Promise<void> {
  const target = targetOf(code);
  if (upToDate(i18n, code, target)) return;
  try {
    await request(i18n, code, target);
  } catch (e) {
    if (!hasUiMessages(i18n, code)) throw e;
  }
  if (!hasUiMessages(i18n, code)) throw new Error(`ui messages not available: ${code}`);
}

/** 60 秒換新（判斷 15）：對目前語系與已載入過的語系，伺服器給的雜湊和已裝上的不同就重取；不提示、失敗下一輪再試。 */
export async function refreshLoadedMessages(i18n: I18nLike, infos: LocaleInfo[]): Promise<void> {
  const jobs = infos
    .filter((l) => (l.code === i18n.global.locale.value || slots.get(l.code)?.loaded !== undefined) && l.uiHash)
    .map((l) => {
      const s = slot(l.code);
      s.wanted = l.uiHash;
      return s.loaded === l.uiHash ? undefined : request(i18n, l.code, l.uiHash).catch(() => undefined);
    });
  await Promise.all(jobs);
}

export type I18nWithT = I18nLike & { global: { t(key: string, named: Record<string, unknown>): string } };

let switchSeq = 0;

/**
 * 切換語系（判斷 17、19）：先載入文字，成功且沒被作廢才套用；失敗提示「無法載入〇〇」並停在原語系。
 * 作廢：之後又切了別的語言；試看切換另要試看世代沒變、試看值仍是這個語系、登入的人沒變。
 * preview：只試看（不記住、不寫瀏覽器快取）；remember: false：套用但不寫 formwright.locale（結束試看時回原語系用）。
 */
export async function switchLocale(i18n: I18nWithT, code: LocaleCode, opts: { preview?: boolean; remember?: boolean } = {}): Promise<boolean> {
  const seq = ++switchSeq;
  const epoch = previewEpoch();
  const user = opts.preview ? useIdentityStore().userId : undefined;
  const stillWanted = () => seq === switchSeq
    && (!opts.preview || (previewEpoch() === epoch && previewLocale() === code && useIdentityStore().userId === user));
  try {
    await ensureCurrentMessages(i18n, code);
  } catch {
    if (!stillWanted()) return false;
    if (opts.preview) setPreviewLocale(null);
    Notify.create({ type: 'negative', message: i18n.global.t('locale.loadFailed', { name: nativeName(code) }) });
    return false;
  }
  if (!stillWanted()) return false;
  applyLocale(code, i18n.global.locale, !opts.preview && opts.remember !== false);
  cacheCurrent(i18n);
  return true;
}

/** 結束試看（選回啟用語系以外的情況：登出、session 失效、guard 驗不過）；畫面正是試看語系時切回記住的語系或預設語系。 */
export function endPreview(i18n: I18nWithT): void {
  const p = previewLocale();
  if (!p) return;
  setPreviewLocale(null);
  if (p && i18n.global.locale.value === p) void switchLocale(i18n, readLocale() ?? defaultLocaleOrNull() ?? FALLBACK_LOCALE, { remember: false });
}

export function readUiCache(): { code: LocaleCode; hash: string; messages: Record<string, string> } | null {
  try {
    const v = JSON.parse(localStorage.getItem(UI_CACHE_KEY) ?? 'null') as unknown;
    if (!v || typeof v !== 'object') return null;
    const { code, hash, messages } = v as Record<string, unknown>;
    if (typeof code !== 'string' || typeof hash !== 'string' || !messages || typeof messages !== 'object' || Array.isArray(messages)) return null;
    return { code, hash, messages: messages as Record<string, string> };
  } catch {
    return null;
  }
}

export function writeUiCache(code: LocaleCode, hash: string, messages: Record<string, string>): void {
  try {
    localStorage.setItem(UI_CACHE_KEY, JSON.stringify({ code, hash, messages }));
  } catch {
    /* 容量不足或不可用：不存 */
  }
}
