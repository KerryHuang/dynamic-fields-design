<script setup lang="ts">
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useGroups, useUsers } from 'src/application/dynamic/queries/permissions-queries';
import type { ApproverSource, ApproverSourceKind } from 'src/domain/dynamic/approval-flow-types';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { ModuleDetail } from 'src/domain/dynamic/types';
import { toSchemaFields } from 'src/presentation/dynamic/designer/schema-fields';
import { defaultSourceOf, hintOf, levelOptions, type ApproverLimits } from './approver-source';

const props = defineProps<{
  modelValue: ApproverSource;
  module: ModuleDetail;
  allowed: ApproverSourceKind[];
  limits: ApproverLimits;
  /** The source as last saved; a manager level above the current policy max stays selectable (tightening only governs what comes after). */
  saved?: ApproverSource | null;
  error?: string;
  label: string;
  disable?: boolean;
  /** Editing a flow's fallback approvers (shows the fallback hint) rather than a stage's source. */
  fallback?: boolean;
}>();
const emit = defineEmits<{ 'update:modelValue': [value: ApproverSource] }>();
const { t, locale } = useI18n();

// Only the list the current source needs is requested (both are cached, a switch of source fetches the other on demand).
const users = useUsers({ enabled: () => props.modelValue.source === 'users' });
const groups = useGroups({ enabled: () => props.modelValue.source !== 'users' && props.modelValue.source !== 'manager' });

type Opt = { value: string | number; label: string };
// Selected items missing from the list: raw code until that list has loaded, "not found or inactive" after (a failed list never claims it).
function withMissing(base: Opt[], selected: (string | number)[], ready: boolean): Opt[] {
  const known = new Set(base.map((o) => o.value));
  return [...base, ...selected.filter((v) => !known.has(v)).map((v) => ({ value: v, label: ready ? t('policies.recipients.missingItem', { item: v }) : String(v) }))];
}
const userBase = computed<Opt[]>(() => (users.data.value ?? []).map((u) => ({ value: u.userId, label: t('policies.recipients.userLabel', { name: u.displayName || u.userId, id: u.userId }) })));
const groupBase = computed<Opt[]>(() => (groups.data.value ?? []).map((g) => ({ value: g.id, label: resolveI18n(g.name, locale.value) })));
const userOptions = computed(() => withMissing(userBase.value, props.modelValue.userIds ?? [], users.isSuccess.value));
const groupOptions = (selected: number[] | null | undefined) => withMissing(groupBase.value, selected ?? [], groups.isSuccess.value);
const userFieldOptions = computed(() => toSchemaFields(props.module).filter((f) => f.type === 'user')
  .map((f) => ({ value: f.id, label: resolveI18n(f.label, locale.value) })));

// Search filter shared by the multi selects (a plain substring match on the label).
const filter = ref('');
const shown = (opts: Opt[]) => { const q = filter.value.trim().toLowerCase(); return q ? opts.filter((o) => o.label.toLowerCase().includes(q)) : opts; };
function onFilter(v: string, update: (fn: () => void) => void) { update(() => { filter.value = v; }); }

const kindOptions = computed(() => props.allowed.map((k) => ({ value: k, label: t(`approval.designer.source.kind.${k}`) })));
const hint = computed(() => hintOf(props.modelValue.source, props.limits, t, props.fallback));
const levels = computed(() => levelOptions(props.limits.maxManagerLevel, props.saved?.source === 'manager' ? props.saved.level : undefined)
  .map((o) => ({ value: o.value, label: t(o.legacy ? 'approval.designer.source.levelOptionLegacy' : 'approval.designer.source.levelOption', { n: o.value }) })));

const set = (patch: Partial<ApproverSource>) => emit('update:modelValue', { ...props.modelValue, ...patch });
const setKind = (k: ApproverSourceKind) => { if (k !== props.modelValue.source) emit('update:modelValue', defaultSourceOf(k)); };
const num = (v: string | number | null) => (v === null || v === '' ? null : Number(v));
const noCandidates = computed(() => props.modelValue.source === 'userField' && !(props.modelValue.candidateGroupIds?.length));
</script>

<template>
  <div class="column no-wrap q-gutter-y-sm" data-testid="approver-source">
    <q-select
      :model-value="modelValue.source" :options="kindOptions" :label="label" emit-value map-options dense outlined :disable="disable"
      data-testid="source-kind" @update:model-value="setKind"
    />

    <q-select
      v-if="modelValue.source === 'users'" :model-value="modelValue.userIds ?? []" :options="shown(userOptions)" :label="t('approval.designer.source.users')"
      multiple use-chips use-input emit-value map-options dense outlined :disable="disable" data-testid="source-users" @filter="onFilter"
      @update:model-value="(v: string[]) => set({ userIds: v })"
    />
    <q-select
      v-else-if="modelValue.source === 'group'" :model-value="modelValue.groupIds ?? []" :options="shown(groupOptions(modelValue.groupIds))" :label="t('approval.designer.source.groups')"
      multiple use-chips use-input emit-value map-options dense outlined :disable="disable" data-testid="source-groups" @filter="onFilter"
      @update:model-value="(v: number[]) => set({ groupIds: v })"
    />
    <template v-else-if="modelValue.source === 'manager'">
      <q-select
        v-if="levels.length" :model-value="modelValue.level" :options="levels" :label="t('approval.designer.source.level')" emit-value map-options dense outlined
        :disable="disable" data-testid="source-level" @update:model-value="(v: number) => set({ level: v })"
      />
      <q-input
        v-else :model-value="modelValue.level ?? ''" type="number" min="1" :label="t('approval.designer.source.level')" dense outlined :disable="disable"
        data-testid="source-level" @update:model-value="(v) => set({ level: num(v) })"
      />
    </template>
    <template v-else-if="modelValue.source === 'userField'">
      <q-select
        :model-value="modelValue.fieldId" :options="userFieldOptions" :label="t('approval.designer.source.field')" emit-value map-options dense outlined
        :disable="disable" :hint="userFieldOptions.length ? undefined : t('approval.designer.source.noUserField')" data-testid="source-field"
        @update:model-value="(v: number) => set({ fieldId: v })"
      />
      <q-select
        :model-value="modelValue.candidateGroupIds ?? []" :options="shown(groupOptions(modelValue.candidateGroupIds))" :label="t('approval.designer.source.candidateGroups')"
        multiple use-chips use-input emit-value map-options dense outlined :disable="disable" data-testid="source-candidates" @filter="onFilter"
        @update:model-value="(v: number[]) => set({ candidateGroupIds: v })"
      />
      <div v-if="noCandidates" class="text-caption text-negative" data-testid="source-candidates-error">{{ t('approval.designer.source.candidatesRequired') }}</div>
    </template>
    <template v-else-if="modelValue.source === 'initiatorPick'">
      <q-select
        :model-value="modelValue.candidateGroupIds ?? []" :options="shown(groupOptions(modelValue.candidateGroupIds))" :label="t('approval.designer.source.candidateGroupsOptional')"
        multiple use-chips use-input emit-value map-options dense outlined :disable="disable" data-testid="source-candidates" @filter="onFilter"
        @update:model-value="(v: number[]) => set({ candidateGroupIds: v })"
      />
      <div class="row q-col-gutter-sm">
        <q-input
          class="col-6" :model-value="modelValue.min ?? ''" type="number" min="1" :label="t('approval.designer.source.min')" dense outlined :disable="disable"
          data-testid="source-min" @update:model-value="(v) => set({ min: num(v) })"
        />
        <q-input
          class="col-6" :model-value="modelValue.max ?? ''" type="number" min="1" :label="t('approval.designer.source.max')" dense outlined :disable="disable"
          data-testid="source-max" @update:model-value="(v) => set({ max: num(v) })"
        />
      </div>
    </template>

    <div v-if="hint" class="text-caption text-muted" data-testid="source-hint">{{ hint }}</div>
    <div v-if="error" class="text-caption text-negative" data-testid="source-error">{{ error }}</div>
  </div>
</template>
