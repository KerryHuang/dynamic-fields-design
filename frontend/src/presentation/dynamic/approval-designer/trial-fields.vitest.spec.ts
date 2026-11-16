import { describe, expect, it } from 'vitest';
import type { ApprovalDefinition, ApprovalFlowInput } from 'src/domain/dynamic/approval-flow-types';
import type { ConditionNode } from 'src/domain/dynamic/types';
import { trialFieldIds } from './trial-fields';

const def = (when: ConditionNode | null, stageWhen: ConditionNode | null, approvers: ApprovalDefinition['stages'][0]['approvers'] = { source: 'manager', level: 1 }): ApprovalDefinition => ({
  shape: 'sequence', when, stages: [{ code: 's', name: {}, when: stageWhen, approvers, mode: 'any', editableFields: [], onEmpty: 'fallback' }],
});
const flow = (key: string, to: number, d: ApprovalDefinition) => ({ key, toOptionId: to, definition: d });
const draft = (to: number, d: ApprovalDefinition): ApprovalFlowInput => ({ key: 'draft', name: {}, toOptionId: to, priority: 1, active: true, definition: d });

describe('trialFieldIds', () => {
  it('collects fields from flow and stage conditions of all flows on the target plus userField sources', () => {
    const flows = [flow('a', 12, def({ all: [{ field: 7, op: 'gte', value: 1 }] }, { any: [{ field: 8, op: 'eq', value: 'x' }, { field: 9, op: 'gt', value: { fieldRef: 10 } }] }))];
    const d = draft(12, def(null, null, { source: 'userField', fieldId: 20 }));
    expect(trialFieldIds(flows, d).sort((x, y) => x - y)).toEqual([7, 8, 9, 10, 20]);
  });
  it('ignores flows on other targets', () => {
    const flows = [flow('a', 13, def({ all: [{ field: 7, op: 'gte', value: 1 }] }, null))];
    expect(trialFieldIds(flows, draft(12, def({ all: [{ field: 5, op: 'eq', value: 1 }] }, null)))).toEqual([5]);
  });
  it('uses the draft instead of the saved flow of the same key', () => {
    const flows = [flow('draft', 12, def({ all: [{ field: 7, op: 'gte', value: 1 }] }, null))];
    expect(trialFieldIds(flows, draft(12, def({ all: [{ field: 8, op: 'gte', value: 1 }] }, null)))).toEqual([8]);
  });
});
