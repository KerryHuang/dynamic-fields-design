import { labelOr } from 'src/domain/ops/ops-format';
import type { McpToken } from 'src/domain/mcp/mcp-types';

type Translate = (key: string, named?: Record<string, unknown>) => string;
type Exists = (key: string) => boolean;

/** 狀態文字：有效、已過期，或「已撤銷（原因）」；沒登記的原因代碼原樣顯示。 */
export function tokenStatusText(t: Translate, te: Exists, k: McpToken): string {
  if (k.status !== 'revoked') return t(`aiTokens.status.${k.status}`);
  const reason = k.revokeReason ? labelOr(t, te, `aiTokens.revokeReasons.${k.revokeReason}`, k.revokeReason) : '';
  return t('aiTokens.status.revoked', { reason });
}

/** 範圍的白話說明；新版後端多了範圍代碼、前端還沒跟上時顯示代碼。 */
export const scopeText = (t: Translate, te: Exists, scope: string) => labelOr(t, te, `aiTokens.scopes.${scope}`, scope);
