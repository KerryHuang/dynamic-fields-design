import { afterEach, describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import enUS from 'src/i18n/en-US.json';
import zhTW from 'src/i18n/zh-TW.json';
import { setBusinessOffset } from 'src/domain/dynamic/business-time';
import { auditDiff, businessDayEndUtc, businessDayStartUtc, formatHealthValue, formatTime, incidentTitle, mailTestError } from './ops-format';

const i18n = (locale: 'zh-TW' | 'en-US') => {
  const g = createI18n({ legacy: false, locale, messages: { 'zh-TW': zhTW, 'en-US': enUS } }).global;
  return { t: (k: string, p?: Record<string, unknown>) => g.t(k, p ?? {}), te: (k: string) => g.te(k) };
};

describe('incidentTitle', () => {
  const { t, te } = i18n('zh-TW');
  it('依標題代碼＋白名單參數組字，與後端 IncidentTitles 的繁中一致', () => {
    expect(incidentTitle(t, te, 'ops.title.serverError', { exceptionType: 'InvalidOperationException', route: 'records/{id}' }))
      .toBe('伺服器錯誤：InvalidOperationException（records/{id}）');
    expect(incidentTitle(t, te, 'ops.title.rulePaused', { ruleKey: 'follow', failures: 5 })).toBe('任務規則「follow」連續失敗 5 次，已自動暫停');
    expect(incidentTitle(t, te, 'ops.title.health.storage.diskFree', { status: 'critical' })).toBe('健康檢查「磁碟剩餘空間」嚴重');
    expect(incidentTitle(t, te, 'ops.title.config.event.tooDeep', { ruleKey: 'e1' })).toBe('規則「e1」設定有問題：連動事件串聯過深');
    expect(incidentTitle(t, te, 'ops.title.channelFailed', { stage: 'auth', serverCode: 535 })).toBe('告警信寄不出去（認證），伺服器回應 535');
    expect(incidentTitle(t, te, 'ops.title.adminChanged', { change: 'granted' })).toBe('系統管理員權限異動：授予');
    expect(incidentTitle(t, te, 'ops.title.privilegeChanged', { change: 'granted' })).toBe('有人取得設計器或權限設定權限');
    expect(incidentTitle(t, te, 'ops.title.privilegeChanged', { change: 'revoked' })).toBe('有人的設計器或權限設定權限被收回');
  });
  it('換語系就換文字；沒登記的代碼回代碼本身', () => {
    const en = i18n('en-US');
    expect(incidentTitle(en.t, en.te, 'ops.title.rulePaused', { ruleKey: 'follow', failures: 5 }))
      .toBe('Task rule "follow" failed 5 times in a row and was paused automatically');
    expect(incidentTitle(t, te, 'ops.title.somethingNew', {})).toBe('ops.title.somethingNew');
    expect(incidentTitle(t, te, 'ops.title.health.future.check', { status: 'odd' })).toBe('健康檢查「future.check」odd');
  });
});

describe('formatHealthValue', () => {
  const { t } = i18n('zh-TW');
  it('萬分比顯示成百分比、位元組換單位、缺值顯示「—」', () => {
    expect(formatHealthValue(1542, 'basisPoints', t, 'zh-TW')).toBe('15.42%');
    expect(formatHealthValue(268435456, 'bytes', t, 'zh-TW')).toBe('256 MB');
    expect(formatHealthValue(26, 'hours', t, 'zh-TW')).toBe('26 小時');
    expect(formatHealthValue(undefined, 'count', t, 'zh-TW')).toBe('—');
  });
});

describe('auditDiff', () => {
  it('物件差異攤成欄位：舊 → 新，其餘鍵留在 rest', () => {
    const r = auditDiff({ before: { enabled: false, host: 'a' }, after: { enabled: true, host: 'a' }, passwordChanged: true });
    expect(r.diff).toEqual([{ field: 'enabled', from: 'false', to: 'true' }]);
    expect(r.rest).toEqual({ passwordChanged: true });
  });
  it('單一值（政策）一行、沒有欄位名；沒有差異時 diff 為空', () => {
    expect(auditDiff({ before: 5, after: 7 }).diff).toEqual([{ field: '', from: '5', to: '7' }]);
    expect(auditDiff({ deleted: { auditLog: 3 } })).toEqual({ diff: [], rest: { deleted: { auditLog: 3 } } });
    expect(auditDiff(undefined)).toEqual({ diff: [], rest: {} });
  });
});

describe('mailTestError', () => {
  const { t, te } = i18n('zh-TW');
  it('422 顯示失敗階段與伺服器回應碼，其他用伺服器訊息', () => {
    expect(mailTestError({ code: 'smtpTestFailed', params: { stage: 'connect' } }, t, te)).toBe('測試信寄不出去：連線');
    expect(mailTestError({ code: 'smtpTestFailed', params: { stage: 'auth', serverCode: 535 } }, t, te)).toBe('測試信寄不出去：認證，伺服器回應 535');
    expect(mailTestError({ code: 'smtpTestTooSoon', message: '請 30 秒後再試' }, t, te)).toBe('請 30 秒後再試');
  });
});

// 稽核與維運的時間照營運時區（使用者 2026-11-20 裁決），不受瀏覽器時區影響
describe('formatTime／businessDayStartUtc／businessDayEndUtc（營運時區）', () => {
  const tz = process.env.TZ;
  afterEach(() => {
    if (tz === undefined) delete process.env.TZ;
    else process.env.TZ = tz;
    setBusinessOffset('+08:00');
  });

  const check = () => {
    setBusinessOffset('+07:00');
    expect(formatTime('2026-11-20T03:00:00Z')).toBe('2026-11-20 10:00 +07:00');
    expect(businessDayStartUtc('2026-11-20')).toBe('2026-11-19T17:00:00.000Z');
    expect(businessDayEndUtc('2026-11-20')).toBe('2026-11-20T16:59:59.999Z');
  };

  it('+07:00', () => check());

  it('瀏覽器時區是 America/New_York 時結果相同', () => {
    process.env.TZ = 'America/New_York';
    check();
  });

  it('缺值與解析不了', () => {
    expect(formatTime(null)).toBe('—');
    expect(formatTime('nope')).toBe('nope');
    expect(formatTime('2026-11-20T03:00:00Z')).toBe('2026-11-20 11:00 +08:00');
  });
});
