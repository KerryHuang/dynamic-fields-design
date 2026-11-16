import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { describe, expect, it } from 'vitest';
import type { ProgressNodeData } from './progress-graph';
import { ADDSIGN_H, ADDSIGN_W, NODE_H, NODE_W } from 'src/domain/dynamic/approval-graph-layout';
import ProgressGraphNode from './ProgressGraphNode.vue';

const data = (p: Partial<ProgressNodeData> = {}): ProgressNodeData => ({
  kind: 'approval', small: false, width: NODE_W, height: NODE_H, label: '主管', kindIcon: 'person', stateIcon: 'hourglass_top', color: 'warning',
  frame: 'solid', strike: false, dim: false, statusText: '簽核中', joinModeText: null, overdue: false, overdueText: '', ariaLabel: '主管 · 簽核中', runIds: [], clickable: false, ...p,
});
// 真的 Handle 會呼叫 useNode()，不在 Vue Flow 裡會丟 TypeError，所以換成 stub。
const mountNode = (d: ProgressNodeData, interactive = true) => mount(ProgressGraphNode, {
  props: { data: d, interactive },
  global: { plugins: [[quasar.Quasar, { components: quasar }]], stubs: { Handle: true } },
});

describe('ProgressGraphNode', () => {
  it('size comes from the layout constants', () => {
    expect(mountNode(data()).attributes('style')).toContain(`width: ${NODE_W}px; height: ${NODE_H}px`);
    const small = mountNode(data({ small: true, width: ADDSIGN_W, height: ADDSIGN_H, kind: 'addSign', label: '加簽' }));
    expect(small.attributes('style')).toContain(`width: ${ADDSIGN_W}px; height: ${ADDSIGN_H}px`);
    expect(small.classes()).toContain('pg-small');
  });

  it('join shows its arrival mode under the name', () => {
    const w = mountNode(data({ kind: 'join', label: '匯合', joinModeText: '任一到齊', statusText: '等待其他分支' }));
    expect(w.get('[data-testid="pg-join-mode"]').text()).toBe('任一到齊');
    expect(w.get('[data-testid="pg-name"]').text()).toBe('匯合');
    expect(mountNode(data()).find('[data-testid="pg-join-mode"]').exists()).toBe(false);
  });

  it('cancelled name is struck through, ended is not', () => {
    const cancelled = mountNode(data({ strike: true, statusText: '已取消', color: 'muted', stateIcon: 'block' }));
    expect(cancelled.get('[data-testid="pg-name-text"]').classes()).toContain('pg-strike');
    const ended = mountNode(data({ strike: false, statusText: '已結束', color: 'muted', stateIcon: 'block' }));
    expect(ended.get('[data-testid="pg-name-text"]').classes()).not.toContain('pg-strike');
  });

  it('overdue active node shows the overdue badge on the status row', () => {
    const w = mountNode(data({ overdue: true, overdueText: '逾期' }));
    expect(w.get('[data-testid="pg-status"] [data-testid="pg-overdue"]').text()).toBe('逾期');
    expect(mountNode(data()).find('[data-testid="pg-overdue"]').exists()).toBe(false);
  });

  it('aria-label carries name and state; no duplicate title for screen readers', () => {
    const w = mountNode(data());
    expect(w.attributes('title')).toBeUndefined();
    expect(w.attributes('aria-label')).toBe('主管 · 簽核中');
  });

  it('shows a state icon and text together, and nothing for a hidden node', () => {
    const shown = mountNode(data());
    expect(shown.get('[data-testid="pg-status"]').text()).toContain('hourglass_top');
    expect(shown.get('[data-testid="pg-status"]').text()).toContain('簽核中');
    const hidden = mountNode(data({ stateIcon: null, color: null, statusText: '', frame: 'neutral', ariaLabel: '財務' }));
    expect(hidden.get('[data-testid="pg-status"]').text()).toBe('');
    expect(hidden.classes()).toContain('pg-c-none');
  });

  it('a not reached node is dimmed and a skipped node has a dashed frame', () => {
    expect(mountNode(data({ dim: true, stateIcon: null, color: null, statusText: '' })).classes()).toContain('pg-dim');
    expect(mountNode(data({ frame: 'dashed', color: 'muted' })).classes()).toContain('pg-frame-dashed');
  });
  describe('clickable nodes', () => {
    it('condition, fork, join, start, end and hidden nodes are not clickable and not focusable; an approval node is a focusable button', () => {
      const on = mountNode(data({ clickable: true, runIds: [1] }));
      expect(on.attributes('role')).toBe('button');
      expect(on.attributes('tabindex')).toBe('0');
      expect(on.attributes('aria-label')).toBe('主管 · 簽核中');
      for (const kind of ['condition', 'fork', 'join', 'start', 'end', 'hidden']) {
        const off = mountNode(data({ kind, clickable: false }));
        expect(off.attributes('tabindex')).toBeUndefined();
        expect(off.attributes('role')).toBe('img');
      }
    });

    it('Enter or Space on a focused approval node opens the detail, and so does a click; other keys do nothing', async () => {
      const w = mountNode(data({ clickable: true, runIds: [1] }));
      await w.trigger('click');
      await w.trigger('keydown', { key: 'Enter' });
      await w.trigger('keydown', { key: ' ' });
      await w.trigger('keydown', { key: 'a' });
      expect(w.emitted('open')).toHaveLength(3);
    });

    it('a node that is not clickable does not emit', async () => {
      const w = mountNode(data({ clickable: false }));
      await w.trigger('click');
      await w.trigger('keydown', { key: 'Enter' });
      expect(w.emitted('open')).toBeUndefined();
    });

    it('in a thumbnail (not interactive) nothing is focusable or clickable', async () => {
      const w = mountNode(data({ clickable: true, runIds: [1] }), false);
      expect(w.attributes('tabindex')).toBeUndefined();
      expect(w.attributes('role')).toBe('img');
      await w.trigger('click');
      expect(w.emitted('open')).toBeUndefined();
    });
  });
});
