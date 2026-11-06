import { mount } from '@vue/test-utils';
import { GridLayout } from 'grid-layout-plus';
import { describe, expect, it, vi } from 'vitest';
import { card } from 'src/pages/dashboards/dashboard-test-kit';
import DashboardGrid from './DashboardGrid.vue';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });

const cards = [card('b', 1, { x: 6, y: 0 }), card('a', 1, { x: 0, y: 4 }), card('c', 1, { x: 0, y: 0 })];
const slots = { card: ({ card: c }: { card: { id: string } }) => `card-${c.id}` };

describe('DashboardGrid', () => {
  it('narrow: one column in reading order, no draggable grid', () => {
    const w = mount(DashboardGrid, { props: { cards, editable: true, narrow: true }, slots });
    expect(w.findComponent(GridLayout).exists()).toBe(false);
    expect(w.findAll('.dashboard-narrow-card').map((e) => e.text())).toEqual(['card-c', 'card-b', 'card-a']);
  });

  it('wide: a 12-column grid that is draggable only while editing', () => {
    const view = mount(DashboardGrid, { props: { cards, editable: false, narrow: false }, slots }).findComponent(GridLayout);
    expect(view.props('colNum')).toBe(12);
    expect(view.props('isDraggable')).toBe(false);
    const edit = mount(DashboardGrid, { props: { cards, editable: true, narrow: false }, slots }).findComponent(GridLayout);
    expect(edit.props('isDraggable')).toBe(true);
    expect(edit.props('isResizable')).toBe(true);
  });

  it('does not emit change on mount or when cards are replaced from outside', async () => {
    const w = mount(DashboardGrid, { props: { cards, editable: true, narrow: false }, slots });
    await w.setProps({ cards: [card('z')] });
    expect(w.emitted('change')).toBeUndefined();
  });
});
