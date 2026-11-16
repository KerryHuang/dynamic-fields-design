import { describe, expect, it } from 'vitest';
import { encodeMentions, mentionQuery, mentionSegments } from './task-mentions';

const sales = { userId: 'sales01', displayName: '業務一' };

describe('encodeMentions', () => {
  it('把 @顯示名稱 換成 @{userId}', () => {
    expect(encodeMentions('請 @業務一 確認', [sales])).toBe('請 @{sales01} 確認');
  });
  it('同一個人出現多次都換', () => {
    expect(encodeMentions('@業務一 @業務一', [sales])).toBe('@{sales01} @{sales01}');
  });
  it('文字裡沒有 @名稱 時原樣不動', () => {
    expect(encodeMentions('沒有了', [sales])).toBe('沒有了');
  });
  it('名稱互為前綴時，長的先比對', () => {
    const picks = [sales, { userId: 'sales10', displayName: '業務一十' }];
    expect(encodeMentions('@業務一十 與 @業務一', picks)).toBe('@{sales10} 與 @{sales01}');
  });
  it('名稱含正規表示式符號也不出錯', () => {
    expect(encodeMentions('@A.B(x) hi', [{ userId: 'ab', displayName: 'A.B(x)' }])).toBe('@{ab} hi');
  });
});

describe('mentionSegments', () => {
  it('認得的 @{id} 變成 mention，不認得的照原文當 text', () => {
    expect(mentionSegments('嗨 @{sales01} @{ghost}', [{ userId: 'sales01', displayName: '業務一', active: false }])).toEqual([
      { kind: 'text', text: '嗨 ' },
      { kind: 'mention', userId: 'sales01', name: '業務一', active: false },
      { kind: 'text', text: ' @{ghost}' },
    ]);
  });
  it('帳號大小寫不同也認得（後端一律小寫）', () => {
    const segs = mentionSegments('@{Sales01}', [{ userId: 'sales01', displayName: '業務一', active: true }]);
    expect(segs).toEqual([{ kind: 'mention', userId: 'sales01', name: '業務一', active: true }]);
  });
  it('格式不對的標記（@{、@{}、@{a b}）整段當文字', () => {
    expect(mentionSegments('@{ @{} @{a b}', [])).toEqual([{ kind: 'text', text: '@{ @{} @{a b}' }]);
  });
  it('空字串沒有片段', () => {
    expect(mentionSegments('', [])).toEqual([]);
  });
});

describe('mentionQuery', () => {
  it('游標前最後一個 @ 之後、沒有空白的字', () => {
    expect(mentionQuery('請 @業', 4)).toBe('業');
  });
  it('@ 前面不是空白或開頭時（像 email）不算', () => {
    expect(mentionQuery('a@b c', 5)).toBeNull();
  });
  it('剛輸入 @ 時查詢是空字串', () => {
    expect(mentionQuery('請 @', 3)).toBe('');
  });
  it('@ 之後已經有空白就結束', () => {
    expect(mentionQuery('@業務一 好', 6)).toBeNull();
  });
});
