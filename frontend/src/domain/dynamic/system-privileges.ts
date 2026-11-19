import type { DesignerFlags, SystemPrivilegeSource, SystemPrivileges } from './types';

/** 設計器動作固定依讀取、新增、修改、刪除的順序（和後端 `DesignerFlags.Names` 一致）。 */
export const DESIGNER_ACTIONS = ['read', 'add', 'edit', 'delete'] as const satisfies readonly (keyof DesignerFlags)[];

export const noSystemPrivileges = (): SystemPrivileges => ({
  designer: { read: false, add: false, edit: false, delete: false }, permissionAdmin: false,
});

/** 深拷貝：對話框改的是草稿，不動查詢快取。缺欄位（舊後端）當成沒有。 */
export const copySystemPrivileges = (p: SystemPrivileges | undefined): SystemPrivileges =>
  p ? { designer: { ...p.designer }, permissionAdmin: p.permissionAdmin } : noSystemPrivileges();

export const sameSystemPrivileges = (a: SystemPrivileges, b: SystemPrivileges) =>
  a.permissionAdmin === b.permissionAdmin && DESIGNER_ACTIONS.every((k) => a.designer[k] === b.designer[k]);

export const designerActions = (d: DesignerFlags) => DESIGNER_ACTIONS.filter((k) => d[k]);

/** 清單小標籤：有任一設計器動作 → 設計器；有權限設定 → 權限設定。 */
export function systemPrivilegeTags(p: SystemPrivileges | undefined): ('designer' | 'permissionAdmin')[] {
  if (!p) return [];
  return [...(designerActions(p.designer).length ? ['designer' as const] : []), ...(p.permissionAdmin ? ['permissionAdmin' as const] : [])];
}

/** 經群組取得的部分：設計器逐群組列出動作，權限設定合併成一份群組名單。系統管理員那筆另有提示，不在這裡。 */
export function inheritedSystemPrivileges(sources: SystemPrivilegeSource[] | undefined) {
  const groups = (sources ?? []).filter((s): s is Extract<SystemPrivilegeSource, { kind: 'group' }> => s.kind === 'group');
  return {
    designer: groups.filter((g) => designerActions(g.designer).length).map((g) => ({ groupName: g.groupName, actions: designerActions(g.designer) })),
    permissionAdmin: groups.filter((g) => g.permissionAdmin).map((g) => g.groupName),
  };
}
