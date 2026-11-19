import type { HealthUnit, IncidentSeverity } from './ops-types';

type Translate = (key: string, params?: Record<string, unknown>) => string;
/** vue-i18n 的 `te`：這個鍵有沒有翻譯。 */
type Exists = (key: string) => boolean;

/** 有翻譯就用翻譯，沒有就回原始代碼（新版後端多了代碼、前端還沒跟上時不顯示鍵名）。 */
export const labelOr = (t: Translate, te: Exists, key: string, fallback: string) => (te(key) ? t(key) : fallback);

const str = (p: Record<string, string | number> | undefined, k: string) => (p?.[k] === undefined ? '' : String(p[k]));

/**
 * 事件標題：代碼＋白名單參數依目前語系組字（維運設計 §17.2.2、O2-T22）。與後端 `IncidentTitles.Text` 同一套組法；
 * 沒登記的標題代碼回代碼本身。
 */
export function incidentTitle(t: Translate, te: Exists, titleKey: string, params?: Record<string, string | number>): string {
  const route = str(params, 'route') ? t('ops.titles.routeSuffix', { route: str(params, 'route') }) : '';
  if (titleKey === 'ops.title.serverError') return t('ops.titles.server', { name: str(params, 'exceptionType') }) + route;
  if (titleKey === 'ops.title.clientError') return t('ops.titles.client', { name: str(params, 'errorName') }) + route;
  if (titleKey === 'ops.title.adminChanged') return t(str(params, 'change') === 'granted' ? 'ops.titles.adminGranted' : 'ops.titles.adminRevoked');
  if (titleKey === 'ops.title.channelFailed') {
    const stage = str(params, 'stage');
    const code = str(params, 'serverCode');
    return t('ops.titles.channel', { stage: labelOr(t, te, `ops.stage.${stage}`, stage) }) + (code ? t('ops.titles.channelCode', { code }) : '');
  }
  if (titleKey === 'ops.title.rulePaused') return t('ops.titles.rulePaused', { rule: str(params, 'ruleKey'), failures: str(params, 'failures') });
  if (titleKey.startsWith('ops.title.config.')) {
    const code = titleKey.slice('ops.title.config.'.length);
    return t('ops.titles.config', { rule: str(params, 'ruleKey'), problem: labelOr(t, te, `ops.ruleError.${code}`, code) });
  }
  if (titleKey.startsWith('ops.title.health.')) {
    const code = titleKey.slice('ops.title.health.'.length);
    const status = str(params, 'status');
    return t('ops.titles.health', { check: checkName(t, te, code), status: labelOr(t, te, `ops.healthStatus.${status}`, status) });
  }
  return titleKey;
}

export const checkName = (t: Translate, te: Exists, code: string) => labelOr(t, te, `ops.checks.${code}.name`, code);

function formatBytes(n: number, locale: string): string {
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let v = n;
  let i = 0;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
  return `${v.toLocaleString(locale, { maximumFractionDigits: i === 0 ? 0 : 1 })} ${units[i]}`;
}

/** 依單位格式化檢查數值：萬分比顯示成百分比、位元組用 KB／MB／GB、時數加單位。 */
export function formatHealthValue(value: number | undefined | null, unit: HealthUnit | string, t: Translate, locale: string): string {
  if (value === undefined || value === null) return '—';
  switch (unit) {
    case 'basisPoints': return `${(value / 100).toLocaleString(locale, { maximumFractionDigits: 2 })}%`;
    case 'bytes': return formatBytes(value, locale);
    case 'hours': return t('ops.unit.hours', { n: value.toLocaleString(locale) });
    case 'count': return t('ops.unit.count', { n: value.toLocaleString(locale) });
    default: return value.toLocaleString(locale);
  }
}

/** 門檻輸入框的單位提示（萬分比換算成百分比給人看）。 */
export function thresholdHint(value: number | null, unit: HealthUnit | string, t: Translate, locale: string): string | undefined {
  if (value === null || !Number.isFinite(value)) return undefined;
  return unit === 'basisPoints' || unit === 'bytes' || unit === 'hours' ? formatHealthValue(value, unit, t, locale) : undefined;
}

/** 時間（UTC ISO）→ 本地 YYYY-MM-DD HH:mm；缺值回「—」。 */
export function formatTime(utc: string | undefined | null): string {
  if (!utc) return '—';
  const d = new Date(utc);
  if (Number.isNaN(d.getTime())) return utc;
  const p = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
}

export const SEVERITY_ICON: Record<IncidentSeverity, { icon: string; color: string }> = {
  critical: { icon: 'error', color: 'negative' },
  warning: { icon: 'warning', color: 'warning' },
  info: { icon: 'info', color: 'info' },
};

/** 健康狀態的圖示與顏色（Quasar 品牌色名，不寫死色碼）。 */
export function statusLook(status: string): { icon: string; color: string } {
  switch (status) {
    case 'ok': return { icon: 'check_circle', color: 'positive' };
    case 'warning': return { icon: 'warning', color: 'warning' };
    case 'critical': return { icon: 'error', color: 'negative' };
    case 'error': return { icon: 'report', color: 'negative' };
    default: return { icon: 'help_outline', color: 'grey' };
  }
}

/** 稽核摘要的差異（`AuditSummary.Diff` 的 `{ before, after }`，只含有變的鍵）攤成「欄位：舊值 → 新值」；其餘鍵原樣留給呼叫端縮排顯示。 */
export interface DiffLine { field: string; from: string; to: string }

const show = (v: unknown): string => (v === undefined ? '' : v === null ? '∅' : typeof v === 'object' ? JSON.stringify(v) : String(v));

export function auditDiff(summary: unknown): { diff: DiffLine[]; rest: Record<string, unknown> } {
  if (!summary || typeof summary !== 'object' || Array.isArray(summary)) return { diff: [], rest: {} };
  const s = { ...(summary as Record<string, unknown>) };
  const diff: DiffLine[] = [];
  if (!('before' in s) && !('after' in s)) return { diff, rest: s };
  const isObj = (v: unknown): v is Record<string, unknown> => !!v && typeof v === 'object' && !Array.isArray(v);
  const before = s.before;
  const after = s.after;
  delete s.before;
  delete s.after;
  if ((before == null || isObj(before)) && (after == null || isObj(after))) {
    const b = (before ?? {}) as Record<string, unknown>;
    const a = (after ?? {}) as Record<string, unknown>;
    for (const k of new Set([...Object.keys(b), ...Object.keys(a)])) {
      if (JSON.stringify(b[k]) !== JSON.stringify(a[k])) diff.push({ field: k, from: show(b[k]), to: show(a[k]) });
    }
  } else {
    // 政策這類單一值：一行，沒有欄位名
    diff.push({ field: '', from: show(before), to: show(after) });
  }
  return { diff, rest: s };
}

/** 測試信失敗的提示：422 `smtpTestFailed` 帶失敗階段與伺服器回應碼（§17.5），其他用伺服器訊息。 */
export function mailTestError(e: { code?: string; message?: string; params?: Record<string, unknown> }, t: Translate, te: Exists): string {
  if (e.code === 'smtpTestFailed') {
    const stage = String(e.params?.stage ?? '');
    const code = e.params?.serverCode;
    return t('ops.mail.testFailed', { stage: labelOr(t, te, `ops.stage.${stage}`, stage) }) + (code ? t('ops.titles.channelCode', { code }) : '');
  }
  return e.message || t('ops.saveFailed');
}

/** 本地日期（YYYY-MM-DD）當天 0 點的 UTC ISO。 */
export const localDayStartUtc = (d: string) => new Date(`${d}T00:00:00`).toISOString();

/** 本地日期（YYYY-MM-DD）的最後一毫秒（UTC ISO）：本地日曆的隔天 0 點減 1 ms。 */
export function localDayEndUtc(d: string): string {
  const day = new Date(`${d}T00:00:00`);
  return new Date(new Date(day.getFullYear(), day.getMonth(), day.getDate() + 1).getTime() - 1).toISOString();
}
