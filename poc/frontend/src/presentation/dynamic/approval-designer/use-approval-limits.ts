import { computed, toValue, type MaybeRefOrGetter } from 'vue';
import { useEffectivePolicies, useModulePolicies } from 'src/application/dynamic/queries/policies-queries';
import type { ModulePolicy, PolicyValue } from 'src/domain/dynamic/policy-types';
import type { ApproverLimits } from './approver-source';

export interface FlowLimits extends ApproverLimits {
  maxStages?: number;
  maxDueHours?: number;
}

const num = (v: PolicyValue | undefined): number | undefined => (typeof v === 'number' ? v : undefined);

/**
 * Every number the designer shows or limits by comes from the module's policies (effective values, `meta.max` for the technical cap);
 * a policy that cannot be read stays undefined, so no number is shown or enforced here and the backend still answers 400.
 * `fallbackPolicy` is the module's default fallback approvers (recipient policies are not in the effective values, so it comes from the module policy list).
 */
export function useApprovalLimits(moduleKey: MaybeRefOrGetter<string>) {
  const effective = useEffectivePolicies(() => toValue(moduleKey));
  const modulePolicies = useModulePolicies(moduleKey);
  const limits = computed<FlowLimits>(() => {
    const v = effective.data.value?.values ?? {};
    const tech = modulePolicies.data.value?.find((p) => p.code === 'approval.maxApprovers')?.max;
    return {
      maxStages: num(v['approval.maxStages']), maxApprovers: num(v['approval.maxApprovers']), maxDueHours: num(v['approval.maxDueHours']),
      maxManagerLevel: num(v['approval.maxManagerLevel']), techMaxApprovers: tech,
    };
  });
  const fallbackPolicy = computed<ModulePolicy | undefined>(() => modulePolicies.data.value?.find((p) => p.code === 'approval.defaultFallbackApprovers'));
  return { limits, fallbackPolicy };
}
