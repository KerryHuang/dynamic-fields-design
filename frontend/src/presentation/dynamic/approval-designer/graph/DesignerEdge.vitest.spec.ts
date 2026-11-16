import { mount } from '@vue/test-utils';
import { beforeAll, describe, expect, it } from 'vitest';
import DesignerEdge from './DesignerEdge.vue';

beforeAll(() => { (SVGElement.prototype as unknown as { getBBox: () => object }).getBBox = () => ({ x: 0, y: 0, width: 40, height: 12 }); });
const base = { id: 'e1', sourceX: 100, sourceY: 50, targetX: 400, targetY: 140, sourcePosition: 'right', targetPosition: 'left' };
const draw = (p: Record<string, unknown>) => mount({ components: { DesignerEdge }, props: ['p'], template: '<svg><DesignerEdge v-bind="p" /></svg>' }, { props: { p: { ...base, ...p } } });
const path = (w: ReturnType<typeof draw>) => w.get('path.vue-flow__edge-path').attributes('d');

describe('DesignerEdge', () => {
  it('designer edges bend where bendX says, not at the midpoint', () => {
    const mid = path(draw({ data: { bendX: 250, fullLabel: '' } }));
    const bent = path(draw({ data: { bendX: 350, fullLabel: '' } }));
    expect(bent).not.toBe(mid);
    expect(bent).toContain('350');
  });

  it('a truncated label keeps the full text in a title', () => {
    const w = draw({ label: '金額大…', data: { bendX: 350, fullLabel: '金額大於十萬' } });
    expect(w.find('title').text()).toBe('金額大於十萬');
    expect(w.text()).toContain('金額大…');
  });

  it('a short label has no title', () => {
    const w = draw({ label: '其他', data: { bendX: 350, fullLabel: '其他' } });
    expect(w.find('title').exists()).toBe(false);
    expect(w.text()).toContain('其他');
  });

  it('a labelled edge draws a background so the line does not cross the text', () => {
    expect(draw({ label: '其他', data: { bendX: 350, fullLabel: '其他' } }).find('.vue-flow__edge-textbg').exists()).toBe(true);
  });
});
