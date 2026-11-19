// 維運監控、稽核、寄信設定的 API 形狀（維運設計 §17.5 與 §17.11–§17.15 實作補記）。伺服器省略 null 屬性，所以選填欄位可能缺席。

export type HealthStatus = 'ok' | 'warning' | 'critical' | 'error' | 'notConfigured' | 'notApplicable' | 'pending';
export type HealthUnit = 'none' | 'basisPoints' | 'bytes' | 'count' | 'hours';
export type IncidentStatus = 'open' | 'acknowledged' | 'resolved';
export type IncidentSeverity = 'info' | 'warning' | 'critical';
export type IncidentCategory = 'config' | 'defect' | 'infra' | 'security';

export interface HealthInstanceState {
  instanceId: string;
  status: string;
  value?: number;
  messageCode?: string;
  checkedUtc: string;
}

export interface HealthCheckState {
  code: string;
  category: string;
  runner: 'watchdog' | 'scheduler';
  scope: 'instance' | 'global';
  status: HealthStatus;
  value?: number;
  unit: HealthUnit;
  messageCode?: string;
  checkedUtc?: string;
  nextDueUtc?: string;
  fromMemory: boolean;
  incidentId?: number;
  /** scope=instance 且有兩個以上在線實例時才有。 */
  instances?: HealthInstanceState[];
}

/** 生效值＋出廠值＋技術範圍；前端不留任何門檻常數（鐵則 2）。 */
export interface Setting<T> {
  value: T;
  factory: T;
  isFactory: boolean;
  min?: number;
  max?: number;
}

export interface HealthCheckConfig {
  code: string;
  category: string;
  runner: string;
  unit: HealthUnit;
  direction: 'higherIsWorse' | 'lowerIsWorse' | 'passFail';
  /** 這個資料庫種類（或 Demo 站）適不適用。 */
  applicable: boolean;
  canDisable: boolean;
  enabled: Setting<boolean>;
  intervalMinutes: Setting<number>;
  warn?: Setting<number>;
  critical?: Setting<number>;
  state: HealthCheckState;
}

/** PUT health-checks/{code}：null＝回到出廠值，欄位省略＝不變。 */
export interface HealthCheckUpdate {
  enabled?: boolean | null;
  intervalMinutes?: number | null;
  warn?: number | null;
  critical?: number | null;
}

export interface HealthResults {
  items: { checkedUtc: string; status: string; value?: number; durationMs: number }[];
  truncated: boolean;
}

export type IncidentHandler = 'designer' | 'it' | 'vendor' | 'admin' | 'contactVendor';

export interface IncidentSummary {
  id: number;
  fingerprint: string;
  source: string;
  category: IncidentCategory;
  severity: IncidentSeverity;
  status: IncidentStatus;
  titleKey: string;
  titleParams?: Record<string, string | number>;
  code: string;
  moduleKey?: string;
  ruleKind?: string;
  ruleId?: number;
  route?: string;
  occurrenceCount: number;
  firstSeenUtc: string;
  lastSeenUtc: string;
  firstVersion: string;
  lastVersion: string;
  regression: boolean;
  reopenCount: number;
  acknowledgedBy?: string;
  acknowledgedUtc?: string;
  resolvedBy?: string;
  resolvedUtc?: string;
  resolvedReason?: string;
  handler: IncidentHandler;
  manualResolvable: boolean;
}

export interface IncidentSample {
  occurredUtc: string;
  requestId?: string;
  userId?: string;
  version?: string;
  route?: string;
  rawMessage?: string;
  rawStack?: string;
  browser?: string;
  locale?: string;
  auditLogId?: number;
}

export interface IncidentDetail extends IncidentSummary {
  normalizedMessage?: string;
  samples: IncidentSample[];
  instances?: HealthInstanceState[];
  history: AuditSummary[];
  historyTruncated: boolean;
}

export interface IncidentList {
  items: IncidentSummary[];
  nextCursor?: string;
}

export interface IncidentFilter {
  status: IncidentStatus[];
  category: IncidentCategory | null;
  severity: IncidentSeverity | null;
}

export interface ScheduledJobInfo {
  jobKey: string;
  lastStartedUtc?: string;
  lastFinishedUtc?: string;
  lastStatus?: string;
  consecutiveFailures: number;
  nextDueUtc: string;
  leaseOwner?: string;
}

export interface OpsOverview {
  overall: 'ok' | 'warning' | 'critical' | 'unknown';
  memoryOnly: boolean;
  pendingIncidentWrites: number;
  pendingDropped: number;
  checks: HealthCheckState[];
  openIncidents: { critical: number; warning: number; info: number; byCategory: Partial<Record<IncidentCategory, number>> };
  needsAttention: IncidentSummary[];
  jobs: ScheduledJobInfo[];
  channel: { kind: 'smtp'; configured: boolean; enabled: boolean; lastSuccessUtc?: string; lastFailureUtc?: string; consecutiveFailures: number };
  version: string;
  commit: string;
  edition: string;
  database: string;
}

export type SmtpSecurity = 'none' | 'startTls' | 'sslOnConnect';

/** 核心寄信設定（帳號信與告警信共用的 SMTP 連線；使用者裁決 2026-11-19）。 */
export interface MailSettings {
  host?: string;
  port: number;
  security: SmtpSecurity;
  userName?: string;
  hasPassword: boolean;
  passwordUnreadable: boolean;
  fromAddress?: string;
  fromName?: string;
  configured: boolean;
  editable: boolean;
}

/** PUT system/mail-settings：password 省略＝不變、""＝清除、其他＝新密碼。 */
export interface MailSettingsInput {
  host: string;
  port: number;
  security: SmtpSecurity;
  userName?: string;
  password?: string;
  fromAddress: string;
  fromName?: string;
}

/** 告警通道只留通道自己的欄位；連線設定在寄信設定。 */
export interface SmtpChannel {
  enabled: boolean;
  toAddresses: string[];
  mailLocale: Setting<string>;
  lastSuccessUtc?: string;
  lastFailureUtc?: string;
  lastError?: string;
  consecutiveFailures: number;
  editable: boolean;
  /** 寄信設定的主機與寄件地址都有。 */
  mailConfigured: boolean;
}

/** PUT system/ops/channels/smtp：mailLocale 省略＝不變、null＝回出廠。 */
export interface SmtpChannelInput {
  enabled: boolean;
  toAddresses: string[];
  mailLocale?: string | null;
}

export interface MailTestResult {
  sentTo: string[];
}

export interface AuditSummary {
  id: number;
  occurredUtc: string;
  actorUserId?: string;
  actorName?: string;
  actorKind: string;
  channel: string;
  tokenId?: number;
  action: string;
  riskLevel: string;
  targetKind?: string;
  targetId?: string;
  targetLabel?: string;
  result: string;
  reasonCode?: string;
  requestId?: string;
  /** Demo 站一律不回。 */
  clientIp?: string;
}

export interface AuditDetail extends AuditSummary {
  summary?: unknown;
  argumentsHash?: string;
  resultCount?: number;
  confirmationId?: number;
  confirmationOutcome?: string;
}

export interface AuditList {
  items: AuditSummary[];
  nextCursor?: string;
}

export interface AuditAction {
  code: string;
  group: string;
  labelKey: string;
  riskLevel: string;
}

/** GET system/ops/audit 的查詢條件；空值不送。 */
export interface AuditFilter {
  fromUtc?: string;
  toUtc?: string;
  actorUserId?: string;
  action?: string;
  actionGroup?: string;
  channel?: string;
  result?: string;
  targetKind?: string;
  targetId?: string;
}

/** 任務規則清單每條規則的暫停欄位（§17.5）。 */
export interface TaskRuleAutoPause {
  pausedUtc: string;
  consecutiveFailures: number;
  lastErrorCode?: string;
  lastRequestId?: string;
  skippedWhilePaused: number;
  pauseCount: number;
}
export interface TaskRuleLastPause {
  pausedUtc: string;
  resumedUtc: string;
  skippedWhilePaused: number;
}
export interface TaskRuleFailureStreak {
  consecutiveFailures: number;
  lastFailureUtc: string;
}
