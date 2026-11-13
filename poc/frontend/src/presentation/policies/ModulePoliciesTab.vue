<script setup lang="ts">
import { Notify } from 'quasar';
import { computed, reactive, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useMe } from 'src/application/dynamic/queries/permissions-queries';
import { useModulePolicies, useSaveModulePolicy } from 'src/application/dynamic/queries/policies-queries';
import { groupByCategory, policyRangeText, policyValueText, samePolicyValue, type ModulePolicy, type PolicyValue } from 'src/domain/dynamic/policy-types';
import type { ModuleDetail } from 'src/domain/dynamic/types';
import PolicyValueEditor from './PolicyValueEditor.vue';
import { useRecipientNames } from './useRecipientNames';

const props = defineProps<{ module: ModuleDetail }>();
const { t } = useI18n();
const me = useMe();
const key = computed(() => props.module.key);
const policies = useModulePolicies(key);
const save = useSaveModulePolicy(key);
const { options: recipientOptions, names } = useRecipientNames();
const canEdit = computed(() => me.data.value?.designer.edit === true);

type Mode = 'inherit' | 'override';
const modes = reactive<Record<string, Mode>>({});
const drafts = reactive<Record<string, PolicyValue>>({});
const errors = reactive<Record<string, string>>({});
const invalid = reactive<Record<string, boolean>>({});
const groups = computed(() => groupByCategory(policies.data.value ?? []));

function seed(p: ModulePolicy) {
  modes[p.code] = p.override == null ? 'inherit' : 'override';
  drafts[p.code] = p.override ?? p.systemValue;
}
watch(() => policies.data.value, (list) => { for (const p of list ?? []) if (!(p.code in modes)) seed(p); }, { immediate: true });

const nameKey = (code: string) => `policies.items.${code}.name`;
const dirty = (p: ModulePolicy) => modes[p.code] === 'inherit'
  ? p.override != null
  : !invalid[p.code] && (p.override == null || !samePolicyValue(drafts[p.code], p.override));

function submit(p: ModulePolicy) {
  errors[p.code] = '';
  save.mutate({ code: p.code, value: modes[p.code] === 'inherit' ? null : drafts[p.code] }, {
    onSuccess: (r) => { seed(r); Notify.create({ type: 'positive', message: t('policies.saved') }); },
    onError: (e) => {
      const err = e as unknown as { errors?: Record<string, string[]>; message?: string };
      errors[p.code] = err.errors?.value?.[0] ?? err.message ?? t('policies.saveFailed');
    },
  });
}
</script>

<template>
  <div>
    <div class="text-muted q-mb-md">{{ t('policies.moduleHint') }}</div>
    <div v-if="policies.isLoading.value" class="text-muted">{{ t('designer.loading') }}</div>
    <div v-if="policies.isError.value" class="text-negative" data-testid="module-policies-load-error">{{ t('policies.loadFailed') }}</div>
    <section v-for="g in groups" :key="g.category" class="q-mb-lg">
      <div class="text-subtitle1 text-weight-medium q-mb-sm">{{ t(`policies.categories.${g.category}`) }}</div>
      <q-card v-for="p in g.items" :key="p.code" flat bordered class="q-mb-md" :data-testid="`policy-row-${p.code}`">
        <q-card-section class="row q-col-gutter-md">
          <div class="col-12 col-md-5">
            <div class="text-weight-medium">{{ t(nameKey(p.code)) }}</div>
            <div class="text-muted text-caption">{{ t(`policies.items.${p.code}.help`) }}</div>
            <div class="text-caption q-mt-sm">
              {{ t('policies.systemValue') }}：{{ policyValueText(p, p.systemValue, t, names) }}
              <template v-if="policyRangeText(p, t)">／{{ t('policies.range') }}：{{ policyRangeText(p, t) }}</template>
            </div>
          </div>
          <div class="col-12 col-md-7">
            <q-radio
              v-model="modes[p.code]" val="inherit" :disable="!canEdit" :data-testid="`policy-inherit-${p.code}`"
              :label="t('policies.inherit', { value: policyValueText(p, p.systemValue, t, names) })"
            />
            <div class="row items-start no-wrap">
              <q-radio v-model="modes[p.code]" val="override" :disable="!canEdit" :data-testid="`policy-override-${p.code}`" :label="t('policies.override')" />
              <div class="col q-pl-sm">
                <PolicyValueEditor
                  v-model="drafts[p.code]" :meta="p" :label="t(nameKey(p.code))" :recipient-options="recipientOptions" :disable="!canEdit || modes[p.code] !== 'override'"
                  @update:valid="(v: boolean) => (invalid[p.code] = !v)"
                />
              </div>
            </div>
            <div v-if="errors[p.code]" class="text-negative text-caption q-mt-xs" :data-testid="`policy-error-${p.code}`">{{ errors[p.code] }}</div>
            <q-btn
              v-if="canEdit" class="q-mt-sm q-px-md" color="primary" unelevated no-caps dense :label="t('policies.save')"
              :disable="!dirty(p) || save.isPending.value" :data-testid="`policy-save-${p.code}`" @click="submit(p)"
            />
          </div>
        </q-card-section>
      </q-card>
    </section>
  </div>
</template>
