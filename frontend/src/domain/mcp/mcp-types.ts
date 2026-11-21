// AI 連線（MCP）個人權杖與管理員頁用的型別（MCP 設計 §13.4）；伺服器省略 null 屬性，所以可空欄位都是可省略。

/** me/mcp 不能用的原因；能用時省略。 */
export type McpReason = 'disabled' | 'notAllowed' | 'policyInvalid' | 'demoSite' | 'accountBlocked';
export type McpTokenStatus = 'active' | 'expired' | 'revoked';
export type McpRevokeReason = 'owner' | 'admin' | 'userDeactivated' | 'passwordReset';

export interface McpClientExamples { claudeCode: string; codex: string }

export interface McpInfo {
  enabled: boolean;
  canUse: boolean;
  reason?: McpReason | null;
  scopes: string[];
  /** 營運時區的日期（YYYY-MM-DD）；預設與上限都由伺服器依政策算好。 */
  defaultExpiresOn: string;
  maxExpiresOn: string;
  serverUrl: string;
  examples: McpClientExamples;
}

export interface McpToken {
  id: number;
  userId: string;
  name: string;
  last4: string;
  scopes: string[];
  createdUtc: string;
  expiresUtc: string;
  expiresOn: string;
  lastUsedUtc?: string | null;
  lastUsedIp?: string | null;
  status: McpTokenStatus;
  revokedUtc?: string | null;
  revokedBy?: string | null;
  revokeReason?: McpRevokeReason | null;
  todayCalls: number;
}

export interface McpTokenCreated { token: string; item: McpToken }

export interface McpTokenCall {
  id: number;
  action: string;
  tool?: string | null;
  occurredUtc: string;
  result: string;
  reasonCode?: string | null;
}

export interface McpCreateBody { name: string; scopes: string[]; expiresOn: string; password: string }
