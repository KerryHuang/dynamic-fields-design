<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import type { ApprovalInboxFilter, ApprovalInstanceStatus } from 'src/domain/dynamic/approval-types';
import { INBOX_STATUSES, type ModuleOption } from './inbox-filter';

const props = defineProps<{ modelValue: ApprovalInboxFilter; showStatus: boolean; modules: ModuleOption[] }>();
const emit = defineEmits<{ 'update:modelValue': [value: ApprovalInboxFilter] }>();
const { t } = useI18n();

const statusOptions = computed(() => INBOX_STATUSES.map((s) => ({ value: s, label: t(`approval.status.${s}`) })));
const set = (patch: Partial<ApprovalInboxFilter>) => emit('update:modelValue', { ...props.modelValue, ...patch });
const text = (v: string | number | null) => (v === null || v === '' ? undefined : String(v));
</script>

<template>
  <div class="row q-col-gutter-sm">
    <div class="col-12 col-sm-6 col-md-3">
      <q-select
        :model-value="modelValue.module ?? null" :options="modules" emit-value map-options clearable outlined dense
        :label="t('approval.center.filters.module')" data-testid="approval-filter-module"
        @update:model-value="(v: string | null) => set({ module: v ?? undefined })"
      />
    </div>
    <div v-if="showStatus" class="col-12 col-sm-6 col-md-3">
      <q-select
        :model-value="modelValue.status ?? null" :options="statusOptions" emit-value map-options clearable outlined dense
        :label="t('approval.center.filters.status')" data-testid="approval-filter-status"
        @update:model-value="(v: ApprovalInstanceStatus | null) => set({ status: v ?? undefined })"
      />
    </div>
    <div class="col-12 col-sm-6 col-md-3">
      <q-input
        :model-value="modelValue.from ?? ''" type="date" stack-label outlined dense clearable
        :label="t('approval.center.filters.from')" data-testid="approval-filter-from"
        @update:model-value="(v) => set({ from: text(v) })"
      />
    </div>
    <div class="col-12 col-sm-6 col-md-3">
      <q-input
        :model-value="modelValue.to ?? ''" type="date" stack-label outlined dense clearable
        :label="t('approval.center.filters.to')" data-testid="approval-filter-to"
        @update:model-value="(v) => set({ to: text(v) })"
      />
    </div>
  </div>
</template>
