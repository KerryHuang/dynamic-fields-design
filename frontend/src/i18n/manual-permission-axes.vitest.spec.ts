import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import zhTW from 'shared/i18n/zh-TW/ui.json';

// 手冊的文案以畫面實際文字為準（.claude/rules/docs.md）：權限軸-b 的畫面句子要原樣出現在手冊裡
const manual = readFileSync(resolve(process.cwd(), '../docs/manual.md'), 'utf8');
const fill = (s: string, params: Record<string, string>) => s.replace(/\{(\w+)\}/g, (_, k: string) => params[k] ?? `{${k}}`);

describe('PAX-37 手冊與畫面文字一致', () => {
  it.each([
    ['systemPrivileges.permissionAdminToggle', zhTW.systemPrivileges.permissionAdminToggle],
    ['systemPrivileges.hint', zhTW.systemPrivileges.hint],
    ['systemPrivileges.adminHint', zhTW.systemPrivileges.adminHint],
    ['systemPrivileges.partialSaved', fill(zhTW.systemPrivileges.partialSaved, { reason: '原因' })],
    ['systemPrivileges.inherited', fill(zhTW.systemPrivileges.inherited, { list: '設計器（讀取、修改）來自『IT』；權限設定來自『IT』' })],
    ['systemPrivileges.deleteImpact', fill(zhTW.systemPrivileges.deleteImpact, { list: '設計器（讀取、修改）、權限設定' })],
    ['designer.permissions.readonlyHint', zhTW.designer.permissions.readonlyHint],
    ['designer.permissions.masterReadonly', zhTW.designer.permissions.masterReadonly],
    ['designer.template.permissionsSkipped', zhTW.designer.template.permissionsSkipped],
    ['events.allowHiddenLocked', zhTW.events.allowHiddenLocked],
    ['events.allowHiddenBlocksSave', zhTW.events.allowHiddenBlocksSave],
    ['demoData.needsPermission', zhTW.demoData.needsPermission],
    ['audit.actions.user.systemPrivileges', zhTW.audit.actions.user.systemPrivileges],
    ['audit.actions.group.systemPrivileges', zhTW.audit.actions.group.systemPrivileges],
    ['ops.titles.privilegeGranted', zhTW.ops.titles.privilegeGranted],
    ['ops.titles.privilegeRevoked', zhTW.ops.titles.privilegeRevoked],
  ])('%s 出現在手冊', (_key, text) => {
    expect(manual).toContain(text);
  });

  it('第 18 章不再寫「系統管理員身分不會自動帶設計器權限」', () => {
    expect(manual).not.toContain('系統管理員身分不會自動帶設計器權限');
  });
});
