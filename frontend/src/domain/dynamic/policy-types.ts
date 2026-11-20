import { installedLocales, nativeName, type LocaleInfo } from 'src/i18n/locale-settings';
import { joinList } from 'src/i18n/translate';

export type PolicyKind = 'integer' | 'boolean' | 'choice' | 'roleSet' | 'codeList' | 'recipients' | 'utcOffset';
export type PolicyValue = number | boolean | string | string[];

export interface PolicyMeta {
  code: string;
  category: string;
  kind: PolicyKind;
  unit?: string;
  min?: number;
  max?: number;
  options?: string[];
  /** 收件人政策另外允許的動態項目（例：`ruleEditor`＝該規則的最後修改者；維運設計 §17.2.5）。 */
  dynamicRecipients?: string[];
}

/** 動態收件人項目的四語標籤鍵（`policies.recipients.dynamic.<項目>`）。 */
export const dynamicRecipientKey = (item: string) => `policies.recipients.dynamic.${item}`;

/** 全系統政策；伺服器省略 null 屬性，所以 `value` 缺席＝沒設（用出廠值）。 */
export interface SystemPolicy extends PolicyMeta {
  scope: 'system' | 'module';
  factory: PolicyValue;
  value?: PolicyValue;
  effective: PolicyValue;
}

/** 模組視角；`override` 缺席＝沿用全系統。 */
export interface ModulePolicy extends PolicyMeta {
  systemValue: PolicyValue;
  override?: PolicyValue;
  effective: PolicyValue;
}

export interface EffectivePolicies {
  moduleKey?: string;
  values: Record<string, PolicyValue>;
  /** 全部已安裝語系（含沒啟用的）的自稱、Quasar 語言包與上層語系 */
  locales: LocaleInfo[];
}

type Translate = (key: string, params?: Record<string, unknown>) => string;

export function samePolicyValue(a?: PolicyValue, b?: PolicyValue): boolean {
  if (Array.isArray(a) && Array.isArray(b)) return a.length === b.length && a.every((x, i) => x === b[i]);
  return a === b;
}

function formatBytes(n: number): string {
  if (n >= 1024 * 1024) return `${(n / 1024 / 1024).toFixed(1)} MB`;
  if (n >= 1024) return `${(n / 1024).toFixed(1)} KB`;
  return `${n} B`;
}

/** 收件人項目（admins、group:<id>、user:<userId>）→ 顯示名稱；對不到的項目由呼叫端以 missing 顯示。 */
export type RecipientNames = ReadonlyMap<string, string>;

/**
 * 語系類政策（locale.default、enabled、requiredText）：選項全是已安裝語系的代碼，顯示各語言自稱、不進翻譯（p3 判斷 24、i4a 判斷 19）。
 * calendar.weekStart 同分類但不是語系。
 */
export const isLocalePolicy = (meta: PolicyMeta): boolean =>
  meta.category === 'locale' && (meta.kind === 'choice' || meta.kind === 'roleSet')
  && !!meta.options?.length && meta.options.every((o) => installedLocales().includes(o));

/** 選項顯示文字：語系類用 API 給的自稱（找不到回代碼），其餘取 policies.choices。 */
export const choiceLabel = (meta: PolicyMeta, o: string, t: Translate): string =>
  isLocalePolicy(meta) ? nativeName(o) : t(`policies.choices.${o}`);

export function policyValueText(meta: PolicyMeta, v: PolicyValue, t: Translate, names?: RecipientNames | undefined): string {
  switch (meta.kind) {
    case 'boolean': return t(v ? 'policies.on' : 'policies.off');
    case 'choice': return choiceLabel(meta, String(v), t);
    // 固定 UTC 偏移不翻譯（計畫判斷 2）
    case 'utcOffset': return `UTC${String(v)}`;
    case 'roleSet': {
      const list = v as string[];
      return list.length ? joinList(list.map((o) => choiceLabel(meta, o, t))) : t('policies.none');
    }
    case 'codeList': return joinList(v as string[]);
    // 名稱還沒載入（names 為 undefined）時顯示原始項目，不誤報找不到
    case 'recipients': return joinList((v as string[]).map((i) => (meta.dynamicRecipients?.includes(i) ? t(dynamicRecipientKey(i))
      : names ? names.get(i) ?? t('policies.recipients.missing') : i)));
    default:
      return meta.unit === 'bytes' ? `${v} (${t('policies.bytesHint', { size: formatBytes(Number(v)) })})` : String(v);
  }
}

export function policyRangeText(meta: PolicyMeta, t: Translate): string | null {
  if (meta.kind === 'integer') return t('policies.rangeInt', { min: meta.min, max: meta.max });
  if (meta.kind === 'codeList') return t('policies.rangeList', { min: meta.min, max: meta.max });
  if (meta.kind === 'recipients') return t('policies.rangeItems', { min: meta.min, max: meta.max });
  return null;
}

export function groupByCategory<T extends PolicyMeta>(items: T[]): { category: string; items: T[] }[] {
  const groups: { category: string; items: T[] }[] = [];
  for (const it of items) {
    const g = groups.find((x) => x.category === it.category);
    if (g) g.items.push(it); else groups.push({ category: it.category, items: [it] });
  }
  return groups;
}
