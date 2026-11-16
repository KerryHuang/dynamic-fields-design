import { describe, expect, it } from 'vitest';
import type { ApprovalFlowWarning, GraphDefinition } from './approval-flow-types';
import { mapGraphIssues } from './approval-graph-errors';

const sent: GraphDefinition = {
  shape: 'graph', stages: [],
  nodes: [{ id: 'start', kind: 'start' }, { id: 'a', kind: 'approval' }, { id: 'BAD id', kind: 'approval' }, { id: 'end', kind: 'end' }],
  edges: [{ id: 'e1', from: 'start', to: 'a' }, { id: 'e2', from: 'a', to: 'end' }, { id: 'BAD', from: 'a', to: 'end' }],
};
const w = (key: string, code = 'W1'): ApprovalFlowWarning => ({ key, code, message: `warn ${key}` });

describe('approval graph error mapping', () => {
  it('node and edge keys map to their ids', () => {
    const r = mapGraphIssues({
      'definition.nodes.a': ['no out'], 'definition.nodes.a.approvers': ['pick'], 'definition.edges.e2.when': ['set it'], 'definition.edges.e2': ['dup'],
    }, [], sent);
    expect(r.errors.nodes.a).toEqual([
      { key: 'definition.nodes.a', field: null, message: 'no out' }, { key: 'definition.nodes.a.approvers', field: 'approvers', message: 'pick' },
    ]);
    expect(r.errors.edges.e2.map((x) => x.field)).toEqual(['when', null]);
    expect(r.errors.flow).toEqual([]);
  });

  it('index keys map through the sent definition', () => {
    const r = mapGraphIssues({ 'definition.nodes[2].id': ['bad code'], 'definition.edges[2].id': ['bad edge code'] }, [], sent);
    expect(r.errors.nodes['BAD id']).toEqual([{ key: 'definition.nodes[2].id', field: 'id', message: 'bad code' }]);
    expect(r.errors.edges.BAD).toEqual([{ key: 'definition.edges[2].id', field: 'id', message: 'bad edge code' }]);
  });

  it('an index outside the sent definition goes to the flow list', () => {
    const r = mapGraphIssues({ 'definition.nodes[9].id': ['x'] }, [], sent);
    expect(r.errors.flow.map((x) => x.message)).toEqual(['x']);
  });

  it('flow-level keys, graph and unmatched definition keys go to the flow list', () => {
    const r = mapGraphIssues({
      key: ['key taken'], name: ['name needed'], toOptionId: ['bad target'], priority: ['dup'], active: ['publish first'],
      'definition.graph': ['cycle'], 'definition.when': ['bad when'], 'definition.rejectTo': ['x'], 'definition.fallbackApprovers': ['y'],
      'definition.nodes.ghost': ['unknown node'], 'definition.edges.ghost.to': ['unknown edge'], 'definition.stages': ['stages not allowed'],
    }, [], sent);
    expect(r.errors.flow.map((x) => x.key)).toEqual([
      'key', 'name', 'toOptionId', 'priority', 'active', 'definition.graph', 'definition.when', 'definition.rejectTo', 'definition.fallbackApprovers',
      'definition.nodes.ghost', 'definition.edges.ghost.to', 'definition.stages',
    ]);
    expect(r.errors.flow[5]).toEqual({ key: 'definition.graph', field: 'graph', message: 'cycle' });
    expect(r.errors.flow[0]).toEqual({ key: 'key', field: 'key', message: 'key taken' });
    expect(Object.keys(r.errors.nodes)).toEqual([]);
    expect(Object.keys(r.errors.edges)).toEqual([]);
  });

  it('warnings are kept apart from errors', () => {
    const r = mapGraphIssues({ 'definition.nodes.a': ['err'] }, [w('definition.nodes.a'), w('definition.edges.e2', 'W2'), w('definition.graph', 'W3')], sent);
    expect(r.errors.nodes.a).toHaveLength(1);
    expect(r.warnings.nodes.a).toEqual([{ key: 'definition.nodes.a', field: null, message: 'warn definition.nodes.a', code: 'W1' }]);
    expect(r.warnings.edges.e2[0].code).toBe('W2');
    expect(r.warnings.flow[0].code).toBe('W3');
    expect(r.errors.edges.e2).toBeUndefined();
  });

  it('empty input gives empty maps', () => {
    expect(mapGraphIssues(undefined, undefined, sent)).toEqual({
      errors: { nodes: {}, edges: {}, flow: [] }, warnings: { nodes: {}, edges: {}, flow: [] },
    });
  });
});
