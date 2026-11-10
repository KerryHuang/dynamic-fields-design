import { resolveI18n } from './i18n-text';
import type { I18nText } from './types';

export interface Department {
  id: number;
  code: string;
  name: I18nText;
  parentId: number | null;
  managerUserId: string | null;
  sortOrder: number;
  active: boolean;
  memberUserIds: string[];
}

export type DepartmentInput = Omit<Department, 'id' | 'memberUserIds'>;

export interface DepartmentNode extends Department {
  children: DepartmentNode[];
  /** 0 for a root. */
  depth: number;
}

const bySortThenCode = (a: Department, b: Department) => a.sortOrder - b.sortOrder || a.code.localeCompare(b.code);

/** Several roots allowed; siblings by sortOrder then code. A department whose parent is missing becomes a root. */
export function buildDepartmentTree(list: Department[]): DepartmentNode[] {
  const ids = new Set(list.map((d) => d.id));
  const children = new Map<number | null, Department[]>();
  for (const d of list) {
    const key = d.parentId !== null && ids.has(d.parentId) ? d.parentId : null;
    children.set(key, [...(children.get(key) ?? []), d]);
  }
  const seen = new Set<number>();
  const build = (parent: number | null, depth: number): DepartmentNode[] =>
    (children.get(parent) ?? []).filter((d) => !seen.has(d.id)).sort(bySortThenCode).map((d) => {
      seen.add(d.id);
      return { ...d, depth, children: build(d.id, depth + 1) };
    });
  return build(null, 0);
}

/** Depth-first display order (the indented list and the tree select). */
export function flattenDepartmentTree(tree: DepartmentNode[]): DepartmentNode[] {
  return tree.flatMap((n) => [n, ...flattenDepartmentTree(n.children)]);
}

/** Every department below `id` (not `id` itself): the "Move to…" menu leaves these out. */
export function descendantIds(list: Department[], id: number): Set<number> {
  const result = new Set<number>();
  const stack = [id];
  while (stack.length) {
    const current = stack.pop()!;
    for (const d of list) {
      if (d.parentId === current && d.id !== id && !result.has(d.id)) {
        result.add(d.id);
        stack.push(d.id);
      }
    }
  }
  return result;
}

/** "Root / … / Department" in the given locale; '' for null or an unknown id. */
export function departmentPath(list: Department[], id: number | null | undefined, locale: string): string {
  const byId = new Map(list.map((d) => [d.id, d]));
  const names: string[] = [];
  const seen = new Set<number>();
  for (let cur = id == null ? undefined : byId.get(id); cur && !seen.has(cur.id); cur = cur.parentId === null ? undefined : byId.get(cur.parentId)) {
    seen.add(cur.id);
    names.unshift(resolveI18n(cur.name, locale));
  }
  return names.join(' / ');
}
