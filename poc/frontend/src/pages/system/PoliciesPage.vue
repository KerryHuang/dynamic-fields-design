<script setup lang="ts">
import { Notify } from 'quasar';
import { computed, reactive, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useMe } from 'src/application/dynamic/queries/permissions-queries';
import { useSaveSystemPolicy, useSystemPolicies } from 'src/application/dynamic/queries/policies-queries';
import ForbiddenNotice from 'src/components/framework/ForbiddenNotice.vue';
import { groupByCategory, policyRangeText, policyValueText, samePolicyValue, type PolicyValue, type SystemPolicy } from 'src/domain/dynamic/policy-types';
import PolicyValueEditor from 'src/presentation/policies/PolicyValueEditor.vue';

const { t } = useI18n();
const me = useMe();
const isAdmin = computed(() => me.data.value?.isSystemAdmin === true);
// 非系統管理員不送請求（API 本來就會回 403）
const policies = useSystemPolicies({ enabled: isAdmin });
const save = useSaveSystemPolicy();

/** 草稿＝沒設時看到出廠值；存檔失敗時保留，讓使用者改。 */
const drafts = reactive<Record<string, PolicyValue>>({});
const errors = reactive<Record<string, string>>({});
const invalid = reactive<Record<string, boolean>>({});
const groups = computed(() => groupByCategory(policies.data.value ?? []));
watch(() => policies.data.value, (list) => {
  for (const p of list ?? []) if (!(p.code in drafts)) drafts[p.code] = p.value ?? p.factory;
}, { immediate: true });

const nameKey = (code: string) => `policies.items.${code}.name`;
const helpKey = (code: string) => `policies.items.${code}.help`;
const dirty = (p: SystemPolicy) => !invalid[p.code] && !samePolicyValue(drafts[p.code], p.effective);

function submit(p: SystemPolicy, value: PolicyValue | null) {
  errors[p.code] = '';
  save.mutate({ code: p.code, value }, {
    onSuccess: (r) => {
      drafts[p.code] = r.value ?? r.factory;
      Notify.create({ type: 'positive', message: t('policies.saved') });
    },
    onError: (e) => {
      const err = e as unknown as { errors?: Record<string, string[]>; message?: string };
      errors[p.code] = err.errors?.value?.[0] ?? err.message ?? t('policies.saveFailed');
    },
  });
}
</script>

<template>
  <q-page padding>
    <ForbiddenNotice v-if="me.data.value && !me.data.value.isSystemAdmin" />
    <template v-else-if="policies.data.value">
      <div class="text-h6 q-mb-xs">{{ t('policies.title') }}</div>
      <div class="text-muted q-mb-md">{{ t('policies.hint') }}</div>
      <section v-for="g in groups" :key="g.category" class="q-mb-lg">
        <div class="text-subtitle1 text-weight-medium q-mb-sm">{{ t(`policies.categories.${g.category}`) }}</div>
        <q-card v-for="p in g.items" :key="p.code" flat bordered class="q-mb-md" :data-testid="`policy-row-${p.code}`">
          <q-card-section class="row q-col-gutter-md">
            <div class="col-12 col-md-6">
              <div class="text-weight-medium">
                {{ t(nameKey(p.code)) }}
                <q-badge v-if="p.scope === 'module'" outline color="primary" class="q-ml-sm">{{ t('policies.moduleOverridable') }}</q-badge>
              </div>
              <div class="text-muted text-caption">{{ t(helpKey(p.code)) }}</div>
              <div class="text-caption q-mt-sm">
                {{ t('policies.current') }}：{{ policyValueText(p, p.effective, t) }}／{{ t('policies.factory') }}：{{ policyValueText(p, p.factory, t) }}
                <template v-if="policyRangeText(p, t)">／{{ t('policies.range') }}：{{ policyRangeText(p, t) }}</template>
              </div>
            </div>
            <div class="col-12 col-md-6">
              <PolicyValueEditor v-model="drafts[p.code]" :meta="p" @update:valid="(v: boolean) => (invalid[p.code] = !v)" :label="t(nameKey(p.code))" :disable="save.isPending.value" />
              <div v-if="errors[p.code]" class="text-negative text-caption q-mt-xs" :data-testid="`policy-error-${p.code}`">{{ errors[p.code] }}</div>
              <div class="row q-gutter-sm q-mt-xs">
                <q-btn
                  color="primary" unelevated no-caps dense class="q-px-md" :label="t('policies.save')" :disable="!dirty(p) || save.isPending.value"
                  :data-testid="`policy-save-${p.code}`" @click="submit(p, drafts[p.code])"
                />
                <q-btn
                  v-if="p.value != null" flat no-caps dense :label="t('policies.reset')" :disable="save.isPending.value"
                  :data-testid="`policy-reset-${p.code}`" @click="submit(p, null)"
                />
              </div>
            </div>
          </q-card-section>
        </q-card>
      </section>
    </template>
    <div v-else-if="policies.isError.value" class="text-negative" data-testid="policies-load-error">{{ t('policies.loadFailed') }}</div>
  </q-page>
</template>
