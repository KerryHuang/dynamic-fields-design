<script setup lang="ts">
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import type { ApprovalInboxFilter } from 'src/domain/dynamic/approval-types';
import ApprovalFilterFields from './ApprovalFilterFields.vue';
import type { ModuleOption } from './inbox-filter';

/** Wide screens: fields in a row that apply at once. Phones: a button opens a full-screen dialog and the draft applies on 套用. */
const props = defineProps<{ modelValue: ApprovalInboxFilter; showStatus: boolean; modules: ModuleOption[]; mobile: boolean }>();
const emit = defineEmits<{ 'update:modelValue': [value: ApprovalInboxFilter] }>();
const { t } = useI18n();

const open = ref(false);
const draft = ref<ApprovalInboxFilter>({});
const activeCount = computed(() => (props.modelValue.module ? 1 : 0) + (props.showStatus && props.modelValue.status ? 1 : 0)
  + (props.modelValue.from ? 1 : 0) + (props.modelValue.to ? 1 : 0));

function openDialog() { draft.value = { ...props.modelValue }; open.value = true; }
function apply() { emit('update:modelValue', { ...draft.value }); open.value = false; }
</script>

<template>
  <div v-if="!mobile" class="q-mb-md" data-testid="approval-filters">
    <ApprovalFilterFields :model-value="modelValue" :show-status="showStatus" :modules="modules" @update:model-value="(v) => emit('update:modelValue', v)" />
  </div>
  <div v-else class="q-mb-md">
    <q-btn outline no-caps color="primary" icon="filter_list" :label="t('approval.center.filters.title')" data-testid="approval-filter-open" @click="openDialog">
      <q-badge v-if="activeCount" color="primary" rounded floating data-testid="approval-filter-count">{{ activeCount }}</q-badge>
    </q-btn>
    <q-dialog v-model="open" maximized>
      <q-card class="column no-wrap">
        <q-card-section class="row items-center no-wrap">
          <div class="text-h6 col">{{ t('approval.center.filters.title') }}</div>
          <q-btn flat dense round icon="close" :aria-label="t('approval.center.filters.close')" @click="open = false" />
        </q-card-section>
        <q-card-section class="col scroll">
          <ApprovalFilterFields v-model="draft" :show-status="showStatus" :modules="modules" />
        </q-card-section>
        <q-card-actions align="right">
          <q-btn flat no-caps :label="t('approval.center.filters.clear')" data-testid="approval-filter-clear" @click="draft = {}" />
          <q-btn color="primary" no-caps :label="t('approval.center.filters.apply')" data-testid="approval-filter-apply" @click="apply" />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </div>
</template>
