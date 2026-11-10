import { describe, expect, it } from 'vitest';
import { buildDepartmentTree, departmentPath, descendantIds, flattenDepartmentTree, type Department } from './organization';

const dept = (id: number, code: string, parentId: number | null, sortOrder = 0): Department => ({
  id, code, name: { 'zh-TW': `名-${code}` }, parentId, managerUserId: null, sortOrder, active: true, memberUserIds: [],
});
// 兩個樹根：exec（sales → sales-1）、board
const LIST = [dept(3, 'sales-1', 2), dept(4, 'board', null, 1), dept(2, 'sales', 1), dept(1, 'exec', null, 0), dept(5, 'ops', 1)];

describe('buildDepartmentTree', () => {
  it('supports several roots ordered by sortOrder then code, with depth', () => {
    const tree = buildDepartmentTree(LIST);
    expect(tree.map((n) => n.code)).toEqual(['exec', 'board']);
    expect(tree[0].children.map((n) => n.code)).toEqual(['ops', 'sales']);
    expect(tree[0].children[1].children[0]).toMatchObject({ code: 'sales-1', depth: 2 });
    expect(tree[0].depth).toBe(0);
  });

  it('treats a department whose parent is missing as a root', () => {
    expect(buildDepartmentTree([dept(9, 'orphan', 99)]).map((n) => n.code)).toEqual(['orphan']);
  });

  it('flattens in display order', () => {
    expect(flattenDepartmentTree(buildDepartmentTree(LIST)).map((n) => `${n.depth}:${n.code}`))
      .toEqual(['0:exec', '1:ops', '1:sales', '2:sales-1', '0:board']);
  });
});

describe('descendantIds', () => {
  it('includes grandchildren but not the department itself', () => {
    expect([...descendantIds(LIST, 1)].sort()).toEqual([2, 3, 5]);
    expect(descendantIds(LIST, 3).size).toBe(0);
  });
});

describe('departmentPath', () => {
  it('joins names from the root', () => {
    expect(departmentPath(LIST, 3, 'zh-TW')).toBe('名-exec / 名-sales / 名-sales-1');
    expect(departmentPath(LIST, null, 'zh-TW')).toBe('');
  });
});
