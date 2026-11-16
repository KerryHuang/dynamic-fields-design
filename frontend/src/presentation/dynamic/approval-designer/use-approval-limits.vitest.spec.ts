import { describe, expect, it, vi } from 'vitest';
import { ref } from 'vue';
import { useApprovalLimits } from './use-approval-limits';

const effective = ref<{ values: Record<string, unknown> } | undefined>();
vi.mock('src/application/dynamic/queries/policies-queries', () => ({
  useEffectivePolicies: () => ({ data: effective }),
  useModulePolicies: () => ({ data: ref([{ code: 'approval.maxApprovers', max: 50 }]) }),
}));

describe('useApprovalLimits', () => {
  it('reads the numbers from the effective policies, including maxBranches', () => {
    effective.value = { values: { 'approval.maxStages': 8, 'approval.maxBranches': 5, 'approval.maxApprovers': 20, 'approval.maxDueHours': 72, 'approval.maxManagerLevel': 3 } };
    const { limits } = useApprovalLimits('po');
    expect(limits.value).toEqual({ maxStages: 8, maxBranches: 5, maxApprovers: 20, maxDueHours: 72, maxManagerLevel: 3, techMaxApprovers: 50 });
  });

  it('a policy that is not there stays undefined', () => {
    effective.value = { values: { 'approval.maxStages': 8 } };
    expect(useApprovalLimits('po').limits.value.maxBranches).toBeUndefined();
    effective.value = undefined;
    expect(useApprovalLimits('po').limits.value.maxBranches).toBeUndefined();
  });
});
