import { describe, expect, it } from 'vitest';
import { isParentCancelled, progressView, subtasksOpenCount } from './task-types';

describe('progressView', () => {
  it('分母是總數減取消數，進度條比例同分母', () => {
    expect(progressView({ done: 2, cancelled: 1, total: 5 })).toEqual({ done: 2, denominator: 4, ratio: 0.5, cancelled: 1 });
    expect(progressView({ done: 0, cancelled: 0, total: 3 })).toMatchObject({ denominator: 3, ratio: 0 });
  });
  it('沒有子任務或全部取消時不顯示（T20）', () => {
    expect(progressView(null)).toBeNull();
    expect(progressView({ done: 0, cancelled: 5, total: 5 })).toBeNull();
  });
});

describe('subtasksOpenCount / isParentCancelled', () => {
  it('只認 409 的 subtasksOpen 並取 params.count', () => {
    expect(subtasksOpenCount({ status: 409, code: 'subtasksOpen', params: { count: 3 } })).toBe(3);
    expect(subtasksOpenCount({ status: 409, code: 'stale' })).toBeNull();
    expect(subtasksOpenCount({ status: 400, code: 'subtasksOpen' })).toBeNull();
    expect(subtasksOpenCount(null)).toBeNull();
  });
  it('parentCancelled 只認 409', () => {
    expect(isParentCancelled({ status: 409, code: 'parentCancelled' })).toBe(true);
    expect(isParentCancelled({ status: 409, code: 'stale' })).toBe(false);
  });
});
