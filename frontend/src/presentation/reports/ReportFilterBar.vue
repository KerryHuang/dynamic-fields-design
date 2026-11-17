<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { NO_RUN_FILTER, REPORT_RANGE_DEFAULT_N, REPORT_RANGE_MAX_N, type ReportRunFilter } from 'src/domain/dynamic/report-types';
import { needsN, REPORT_RANGE_KINDS } from './designer/designer-state';
import DepartmentTickDialog from './DepartmentTickDialog.vue';

/**
 * 報表頁上方的暫時篩選（看板設計 §8.2）：期間（取代報表設定的區間，作用在主要日期欄位）與部門（只有任務資料來源）。
 * 只是頁面狀態，不存檔、不進網址；換報表由父層重置。窄螢幕自動換行。
 */
const props = defineProps<{ modelValue: ReportRunFilter; showDepartment: boolean; rangeAvailable: boolean }>();
const emit = defineEmits<{ 'update:modelValue': [value: ReportRunFilter] }>();
const { t } = useI18n();

const kind = computed(() => props.modelValue.range?.kind ?? '');
const rangeOptions = computed(() => [
  { value: '', label: t('reports.filter.periodDefault') },
  ...REPORT_RANGE_KINDS.map((k) => ({ value: k, label: t(`reports.designer.range.${k}`) })),
]);
function pickRange(k: string) {
  emit('update:modelValue', { ...props.modelValue, range: k ? { kind: k, n: needsN({ kind: k }) ? (props.modelValue.range?.n ?? REPORT_RANGE_DEFAULT_N) : null } : null });
}
// N 打字時只改輸入框，停 500ms 或離開輸入框、按 Enter 才套用（避免每個按鍵都重跑報表）；
// 超出 1 到上限的整數直接夾進範圍，空白或不是整數就還原成目前有效的 N
const N_DEBOUNCE_MS = 500;
const nDraft = ref<string | number>(props.modelValue.range?.n ?? '');
watch(() => props.modelValue.range?.n, (n) => { nDraft.value = n ?? ''; });
let nTimer: ReturnType<typeof setTimeout> | undefined;
const clearNTimer = () => { if (nTimer !== undefined) { clearTimeout(nTimer); nTimer = undefined; } };
function commitN() {
  clearNTimer();
  const range = props.modelValue.range;
  if (!range) return;
  const raw = Number(nDraft.value);
  if (nDraft.value === '' || !Number.isInteger(raw)) { nDraft.value = range.n ?? ''; return; }
  const n = Math.min(REPORT_RANGE_MAX_N, Math.max(1, raw));
  nDraft.value = n;
  if (n !== range.n) emit('update:modelValue', { ...props.modelValue, range: { ...range, n } });
}
function typeN(v: string | number | null) {
  nDraft.value = v ?? '';
  clearNTimer();
  nTimer = setTimeout(commitN, N_DEBOUNCE_MS);
}
onBeforeUnmount(clearNTimer);

const deptOpen = ref(false);
const count = computed(() => props.modelValue.departmentIds.length);
function applyDepartments(ids: number[], includeSub: boolean) {
  deptOpen.value = false;
  emit('update:modelValue', { ...props.modelValue, departmentIds: ids, includeSubDepartments: includeSub });
}
const active = computed(() => !!props.modelValue.range || count.value > 0);
const clearAll = () => emit('update:modelValue', { ...NO_RUN_FILTER });
</script>

<template>
  <div class="row items-start q-col-gutter-sm q-mb-md" data-testid="report-filter-bar">
    <div class="col-12 col-sm-6 col-md-3">
      <q-select
        dense outlined emit-value map-options :label="t('reports.filter.period')" :options="rangeOptions" :disable="!rangeAvailable"
        :model-value="kind" data-testid="filter-range" @update:model-value="pickRange"
      />
      <div v-if="!rangeAvailable" class="text-caption text-muted" data-testid="filter-range-na">{{ t('reports.filter.noPrimaryDate') }}</div>
    </div>
    <div v-if="modelValue.range && needsN(modelValue.range)" class="col-6 col-sm-3 col-md-2">
      <q-input
        dense outlined type="number" min="1" :max="REPORT_RANGE_MAX_N" :label="t('reports.designer.rangeN')" :model-value="nDraft"
        data-testid="filter-range-n" @update:model-value="typeN" @blur="commitN" @keyup.enter="commitN"
      />
    </div>
    <div v-if="showDepartment" class="col-12 col-sm-auto">
      <q-btn
        outline no-caps icon="account_tree" :label="count ? t('reports.filter.departmentSome', { n: count }) : t('reports.filter.departmentAll')"
        :color="count ? 'primary' : undefined" data-testid="filter-department" @click="deptOpen = true"
      />
      <div v-if="count" class="text-caption text-muted" data-testid="filter-department-note">
        {{ modelValue.includeSubDepartments ? t('reports.filter.withSub') : t('reports.filter.withoutSub') }}
      </div>
    </div>
    <div v-if="active" class="col-12 col-sm-auto">
      <q-btn flat dense no-caps color="primary" icon="filter_alt_off" :label="t('reports.filter.clearAll')" data-testid="filter-clear" @click="clearAll" />
    </div>
    <DepartmentTickDialog
      v-if="showDepartment" :open="deptOpen" :selected="modelValue.departmentIds" :include-sub="modelValue.includeSubDepartments"
      @apply="applyDepartments" @close="deptOpen = false"
    />
  </div>
</template>
