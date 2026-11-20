/** 語系包匯入與語系管理（語系包設計 §6；i4d1）的 API 型別。伺服器明示 null 的欄位在這裡是 `| null`，不是可省略。 */

export interface LocalePackRejectedItem { key: string; reason: string; args: string[] }

/** 一段（介面文字或後端訊息）的筆數；extraKeys、rejectedItems 只含前幾筆，總數看 extra、rejected。 */
export interface LocalePackSection {
  added: number; changed: number; unchanged: number; skipped: number; sameAsBuiltin: number;
  extra: number; extraKeys: string[]; rejected: number; rejectedItems: LocalePackRejectedItem[];
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
  completeness: { ui: { have: number; total: number }; server: { have: number; total: number } };
  designData: { count: number };
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
}

export interface LocaleAdminList {
  importEnabled: boolean;
  packMaxBytes: number;
  maxInstalled: number;
  locales: LocaleAdminEntry[];
}
