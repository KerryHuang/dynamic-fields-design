import { joinList } from 'src/i18n/translate';

export type PolicyKind = 'integer' | 'boolean' | 'choice' | 'roleSet' | 'codeList' | 'recipients';
export type PolicyValue = number | boolean | string | string[];

export interface PolicyMeta {
  code: string;
  category: string;
  kind: PolicyKind;
  unit?: string;
  min?: number;
  max?: number;
  options?: string[];
}

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

export function policyValueText(meta: PolicyMeta, v: PolicyValue, t: Translate, names?: RecipientNames | undefined): string {
  switch (meta.kind) {
    case 'boolean': return t(v ? 'policies.on' : 'policies.off');
    case 'choice': return t(`policies.choices.${String(v)}`);
    case 'roleSet': {
      const list = v as string[];
      return list.length ? joinList(list.map((o) => t(`policies.choices.${o}`))) : t('policies.none');
    }
    case 'codeList': return joinList(v as string[]);
    // 名稱還沒載入（names 為 undefined）時顯示原始項目，不誤報找不到
    case 'recipients': return joinList((v as string[]).map((i) => (names ? names.get(i) ?? t('policies.recipients.missing') : i)));
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
