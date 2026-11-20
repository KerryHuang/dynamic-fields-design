/** 語系包匯入與語系管理（語系包設計 §6；i4d1）的 API 型別。伺服器明示 null 的欄位在這裡是 `| null`，不是可省略。 */

export interface LocalePackRejectedItem { key: string; reason: string; args: string[] }

/** 一段（介面文字或後端訊息）的筆數；extraKeys、rejectedItems 只含前幾筆，總數看 extra、rejected。 */
export interface LocalePackSection {
  added: number; changed: number; unchanged: number; skipped: number; sameAsBuiltin: number;
  extra: number; extraKeys: string[]; rejected: number; rejectedItems: LocalePackRejectedItem[];
  /** 原文已更新的覆寫句，這份檔原樣再匯入而被確認的句數。 */
  confirmed: number;
}

/** 完整度計數與缺最多的分類（清單與預覽報告共用同一個定義，後端算好）。 */
export interface LocaleCompleteness {
  ui: { have: number; total: number };
  server: { have: number; total: number };
  /** 缺最多的分類，至多 5 組；segment 是 ui 或 server。 */
  topMissing: { segment: 'ui' | 'server'; category: string; missing: number }[];
}

/** 一段（介面文字或後端訊息）的覆寫檢視：計數是全部，鍵清單只列前 50 筆。ui 的 runtimeFallback 恆為 0。 */
export interface LocaleReviewSection {
  baseChanged: number; paramsChanged: number; runtimeFallback: number; orphaned: number;
  baseChangedKeys: string[]; paramsChangedKeys: string[]; runtimeFallbackKeys: string[]; orphanedKeys: string[];
}

export interface LocaleReview { ui: LocaleReviewSection; server: LocaleReviewSection }

/** 樣板匯出的選項（GET i18n/locales/{code}/template 的查詢參數）。 */
export interface LocaleTemplateOptions {
  scopes: ('ui' | 'server')[];
  onlyMissing: boolean;
  /** 原文已更新或參數不符的句子（後端取聯集）。 */
  onlyBaseChanged: boolean;
  source: string;
  /** 空字串＝不附參考段。 */
  reference: string;
  /** 以下三項只在目標語系尚未安裝時送。 */
  nativeName?: string;
  fallback?: string;
  quasarLang?: string;
}

export interface LocalePackReportData {
  /** Demo（不暫存）時是 null：不能套用。 */
  previewId: string | null;
  expiresUtc: string | null;
  importEnabled: boolean;
  fileName: string;
  fileHash: string;
  locale: {
    code: string; isNew: boolean; builtin: boolean; nativeName: string;
    quasarLang: string | null; fallbackCode: string | null; changes: string[]; ignored: string[];
  };
  pack: { version?: string; builtFor?: string; baseLocale?: string; note?: string };
  ui: LocalePackSection;
  server: LocalePackSection;
  completeness: LocaleCompleteness;
  designData: { count: number };
  /** 這次套用會清掉的已失效覆寫句數（只在會寫新版本時非 0）。 */
  orphanedDropped: { ui: number; server: number };
  warnings: { code: string; args: string[] }[];
}

export interface ApplyLocalePackResult { code: string; isNew: boolean; noChanges: boolean; packId: number | null; enabled: boolean }

export interface LocaleAdminEntry {
  code: string;
  nativeName: string;
  origin: 'builtin' | 'installed';
  fallbackCode: string | null;
  enabled: boolean;
  isDefault: boolean;
  required: boolean;
  overrides: { ui: number; server: number };
  /** 沒有任何覆寫版本時是 null；移除覆寫產生的空版本 packVersion、fileName 都是 null。 */
  current: { packVersion: string | null; fileName: string | null; importedUtc: string; importedBy: string | null } | null;
  hasPrevious: boolean;
  completeness: LocaleCompleteness;
  review: LocaleReview;
  /** 目前版語系包產生時的平台版本；沒有時明示 null。 */
  builtFor: string | null;
}

export interface LocaleAdminList {
  importEnabled: boolean;
  packMaxBytes: number;
  maxInstalled: number;
  locales: LocaleAdminEntry[];
}
