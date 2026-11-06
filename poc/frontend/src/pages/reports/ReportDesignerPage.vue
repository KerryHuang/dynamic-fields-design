<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRoute, useRouter } from 'vue-router';
import { useReport, useReportCatalog, useReportCatalogModules, usePreviewReport, useSaveReport } from 'src/application/dynamic/queries/reports-queries';
import ForbiddenNotice from 'src/components/framework/ForbiddenNotice.vue';
import { isForbidden, type ApiError } from 'src/services/api/http';
import { canAdvance, canSave, emptyState, fromDefinition, toDefinition, unavailableRefs, type DesignerStep } from 'src/presentation/reports/designer/designer-state';
import StepFields from 'src/presentation/reports/designer/StepFields.vue';
import StepFilters from 'src/presentation/reports/designer/StepFilters.vue';
import StepGroups from 'src/presentation/reports/designer/StepGroups.vue';
import StepPreview from 'src/presentation/reports/designer/StepPreview.vue';
import StepSource from 'src/presentation/reports/designer/StepSource.vue';

const { t, locale } = useI18n();
const route = useRoute();
const router = useRouter();
const reportId = computed(() => (route.params.id ? Number(route.params.id) : NaN));
const isEdit = computed(() => !Number.isNaN(reportId.value));

const state = reactive(emptyState());
const step = ref<DesignerStep>(0);

// 編輯：載入一次，之後以設計器裡的狀態為準
const report = useReport(reportId);
const loaded = ref(false);
watch(() => report.data.value, (d) => {
  if (!d || loaded.value) return;
  loaded.value = true;
  Object.assign(state, fromDefinition(d.definition, d.name));
}, { immediate: true });
const forbidden = computed(() => isForbidden(report.error.value));
const notOwner = computed(() => !!report.data.value && !report.data.value.isOwner);
// 遮蔽過的定義存回去會得到 400，所以擁有者也只能看原因
const hiddenBlocked = computed(() => loaded.value && state.hasHidden);

const modules = useReportCatalogModules(locale);
const catalogQuery = useReportCatalog(computed(() => state.mainModuleKey), locale);
const catalog = computed(() => catalogQuery.data.value ?? null);
const unavailable = computed(() => (catalog.value ? unavailableRefs(state, catalog.value) : unavailableRefs(state, { fields: [], via: [], children: [] })));

const STEPS = ['source', 'fields', 'filters', 'groups', 'preview'] as const;
const blocked = computed(() => step.value >= 3 && unavailable.value.any);
const nextEnabled = computed(() => canAdvance(state, step.value) && !!catalog.value && !blocked.value);
const messages = (e: unknown) => {
  const err = e as ApiError;
  const list = Object.values(err.errors ?? {}).flat();
  return list.length ? list : [t('reports.saveFailed')];
};

const preview = usePreviewReport();
const previewErrors = ref<string[]>([]);
function runPreview() {
  previewErrors.value = [];
  preview.mutate({ definition: toDefinition(state), locale: locale.value }, { onError: (e) => { previewErrors.value = messages(e); } });
}
function go(to: DesignerStep) {
  step.value = to;
  if (to === 4) runPreview();
}

const save = useSaveReport();
const saveErrors = ref<string[]>([]);
const savable = computed(() => !!catalog.value && canSave(state, catalog.value));
function doSave() {
  saveErrors.value = [];
  save.mutate(
    { id: isEdit.value ? reportId.value : null, name: state.name.trim(), definition: toDefinition(state) },
    { onSuccess: (d) => { void router.push(`/reports/${d.id}`); }, onError: (e) => { saveErrors.value = messages(e); } },
  );
}
</script>

<template>
  <q-page padding>
    <ForbiddenNotice v-if="forbidden" />
    <div v-else-if="isEdit && report.isError.value" class="text-negative" data-testid="designer-load-error">{{ t('reports.loadFailed') }}</div>
    <div v-else-if="isEdit && notOwner" data-testid="designer-not-owner">{{ t('reports.designer.notOwner') }}</div>
    <template v-else-if="!isEdit || loaded">
      <div class="row items-center q-mb-md q-gutter-x-sm">
        <q-btn flat dense icon="arrow_back" :to="isEdit ? `/reports/${reportId}` : '/reports'" :aria-label="t('reports.back')" />
        <div class="text-h6">{{ isEdit ? t('reports.designer.editTitle') : t('reports.designer.newTitle') }}</div>
      </div>

      <div v-if="hiddenBlocked" class="text-negative" data-testid="designer-hidden-blocked">{{ t('reports.designer.hiddenBlocked') }}</div>
      <template v-else>
        <div class="row q-gutter-x-md q-mb-md">
          <span
            v-for="(k, i) in STEPS" :key="k" :class="i === step ? 'text-weight-bold text-primary' : 'text-grey-7'" :data-testid="`step-label-${i}`"
          >{{ i + 1 }}. {{ t(`reports.designer.steps.${k}`) }}</span>
        </div>
        <div v-if="unavailable.any" class="text-negative q-mb-md" data-testid="designer-unavailable">{{ t('reports.designer.unavailableBanner') }}</div>
        <div v-if="catalogQuery.isError.value" class="text-negative q-mb-md" data-testid="catalog-error">{{ t('reports.designer.catalogFailed') }}</div>

        <StepSource v-if="step === 0" :state="state" :modules="modules.data.value ?? []" :catalog="catalog" :unavailable="unavailable" />
        <template v-else-if="catalog">
          <StepFields v-if="step === 1" :state="state" :catalog="catalog" :unavailable="unavailable" />
          <StepFilters v-else-if="step === 2" :state="state" :catalog="catalog" :unavailable="unavailable" />
          <StepGroups v-else-if="step === 3" :state="state" :catalog="catalog" :unavailable="unavailable" />
          <StepPreview
            v-else :state="state" :result="preview.data.value ?? null" :loading="preview.isPending.value" :errors="previewErrors"
            :save-errors="saveErrors" :can-save="savable" :saving="save.isPending.value" @save="doSave" @refresh="runPreview"
          />
        </template>

        <div class="row q-gutter-x-sm q-mt-md">
          <q-btn v-if="step > 0" flat no-caps :label="t('reports.designer.prev')" data-testid="designer-prev" @click="go((step - 1) as DesignerStep)" />
          <q-btn
            v-if="step < 4" color="primary" no-caps :label="t('reports.designer.next')" :disable="!nextEnabled" data-testid="designer-next"
            @click="go((step + 1) as DesignerStep)"
          />
        </div>
      </template>
    </template>
  </q-page>
</template>
