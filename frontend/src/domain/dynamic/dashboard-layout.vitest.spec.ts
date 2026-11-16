import { describe, expect, it } from 'vitest';
import { addCard, applyPositions, nextCardId, readingOrder, removeCard, sameLayout, updateCard } from './dashboard-layout';
import type { DashboardCard, DashboardLayout } from './dashboard-types';

const c = (id: string, x = 0, y = 0, over: Partial<DashboardCard> = {}): DashboardCard =>
  ({ id, reportId: 1, viz: 'table', x, y, w: 6, h: 4, options: { sortDesc: false, cumulative: false }, ...over });

describe('dashboard-layout', () => {
  it('addCard puts the card at the bottom-left with the viz default size and options', () => {
    const next = addCard({ cards: [c('c1', 0, 0), c('c2', 6, 2, { h: 3 })] }, 9, 'indicator');
    const added = next.cards[2];
    expect(added).toMatchObject({ reportId: 9, viz: 'indicator', x: 0, y: 5, w: 4, h: 2, options: { sortDesc: false, cumulative: false } });
  });

  it('nextCardId never reuses an id after a removal', () => {
    const layout: DashboardLayout = { cards: [c('c2'), c('c3')] };
    expect(nextCardId(layout)).toBe('c4');
    expect(nextCardId({ cards: [] })).toBe('c1');
  });

  it('removeCard and updateCard only touch the named card', () => {
    const layout = { cards: [c('a'), c('b')] };
    expect(removeCard(layout, 'a').cards.map((x) => x.id)).toEqual(['b']);
    const upd = updateCard(layout, 'b', { title: '標題', viz: 'bar' });
    expect(upd.cards[0]).toEqual(layout.cards[0]);
    expect(upd.cards[1]).toMatchObject({ title: '標題', viz: 'bar' });
  });

  it('applyPositions leaves cards that are not listed untouched', () => {
    const layout = { cards: [c('a'), c('b', 6, 0)] };
    const next = applyPositions(layout, [{ i: 'a', x: 2, y: 1, w: 3, h: 5 }]);
    expect(next.cards[0]).toMatchObject({ x: 2, y: 1, w: 3, h: 5 });
    expect(next.cards[1]).toEqual(layout.cards[1]);
  });

  it('readingOrder is top to bottom, then left to right', () => {
    expect(readingOrder([c('b', 6, 0), c('a', 0, 4), c('c', 0, 0)]).map((x) => x.id)).toEqual(['c', 'b', 'a']);
  });

  it('sameLayout ignores card order but not positions', () => {
    const a = { cards: [c('a', 0, 0), c('b', 6, 0)] };
    expect(sameLayout(a, { cards: [c('b', 6, 0), c('a', 0, 0)] })).toBe(true);
    expect(sameLayout(a, { cards: [c('a', 1, 0), c('b', 6, 0)] })).toBe(false);
  });
});
