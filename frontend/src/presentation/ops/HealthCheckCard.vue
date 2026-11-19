<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useHealthResults, useRunHealthCheck, useUpdateHealthCheck } from 'src/application/dynamic/queries/ops-queries';
import { checkName, formatHealthValue, formatTime, labelOr, statusLook, thresholdHint } from 'src/domain/ops/ops-format';
import type { HealthCheckConfig, HealthCheckUpdate, Setting } from 'src/domain/ops/ops-types';
import type { ApiError } from 'src/services/api/http';
import HealthTrendChart from './HealthTrendChart.vue';

// 一個健康檢查一張卡（維運設計 §17.3 第 3 項）：狀態、數值、上次與下次時間、立即檢查；展開看趨勢與設定。
// 設定顯示出廠值與允許範圍，留空＝出廠值（送 null）；範圍與出廠值都從 API 取，前端不留門檻常數（鐵則 2）。
const props = defineProps<{ check: HealthCheckConfig }>();
const { t, te, locale } = useI18n();
const $q = useQuasar();

const s = computed(() => props.check.state);
const demoSite = computed(() => s.value.status === 'notApplicable' && s.value.messageCode === 'demoSite');
const statusText = computed(() => (demoSite.value ? t('ops.health.demoSite') : labelOr(t, te, `ops.healthStatus.${s.value.status}`, s.value.status)));
const look = computed(() => statusLook(s.value.status));
const fmt = (v: number) => formatHealthValue(v, props.check.unit, t, locale.value);
const messageText = computed(() => (s.value.messageCode && !demoSite.value ? labelOr(t, te, `ops.health.message.${s.value.messageCode}`, s.value.messageCode) : ''));

const run = useRunHealthCheck();
function runNow() {
  run.mutate(props.check.code, {
    onSuccess: () => $q.notify({ type: 'positive', message: t('ops.health.ran') }),
    onError: (e) => {
      const err = e as unknown as ApiError;
      const key = `ops.health.errors.${err.code}`;
      $q.notify({ type: 'negative', message: err.code && te(key) ? t(key) : err.message || t('ops.saveFailed') });
    },
  });
}

const tab = ref<'trend' | 'settings'>('trend');
const expanded = ref(false);
const range = ref<24 | 168>(24);
const results = useHealthResults(() => props.check.code, range, () => expanded.value && tab.value === 'trend' && props.check.applicable);
const points = computed(() => (results.data.value?.items ?? []).map((p) => ({ time: new Date(p.checkedUtc).getTime(), value: p.value ?? null })));

// 設定草稿：數字欄用字串保留使用者正在打的內容，空白＝出廠值
const draft = ref({ enabled: true, interval: '', warn: '', critical: '' });
const fromSetting = (x?: Setting<number>) => (x && !x.isFactory ? String(x.value) : '');
function reset() {
  draft.value = {
    enabled: props.check.enabled.value, interval: fromSetting(props.check.intervalMinutes), warn: fromSetting(props.check.warn),
    critical: fromSetting(props.check.critical),
  };
}
watch(() => props.check, reset, { immediate: true });
const num = (v: string) => (v.trim() === '' ? null : Number(v));
const invalid = computed(() => [draft.value.interval, draft.value.warn, draft.value.critical].some((v) => v.trim() !== '' && !Number.isInteger(Number(v))));
const errors = ref<Record<string, string[]>>({});
const update = useUpdateHealthCheck();
function save() {
  const body: HealthCheckUpdate = { intervalMinutes: num(draft.value.interval) };
  // 和出廠值相同就送 null（回到出廠），日後出廠值改了跟著變
  if (props.check.canDisable) body.enabled = draft.value.enabled === props.check.enabled.factory ? null : draft.value.enabled;
  if (props.check.warn) body.warn = num(draft.value.warn);
  if (props.check.critical) body.critical = num(draft.value.critical);
  errors.value = {};
  update.mutate({ code: props.check.code, body }, {
    onSuccess: () => $q.notify({ type: 'positive', message: t('ops.saved') }),
    onError: (e) => {
      const err = e as unknown as ApiError;
      errors.value = err.errors ?? {};
      if (!err.errors) $q.notify({ type: 'negative', message: err.message || t('ops.saveFailed') });
    },
  });
}
const err = (k: string) => (errors.value[k] ?? []).join('；');
const factoryHint = (x: Setting<number> | undefined, value: string) => {
  if (!x) return '';
  const base = t('ops.health.factoryRange', { factory: fmt(x.factory), min: fmt(x.min ?? 0), max: fmt(x.max ?? 0) });
  const h = thresholdHint(num(value), props.check.unit, t, locale.value);
  return h ? `${h} · ${base}` : base;
};
const intervalHint = computed(() => {
  const x = props.check.intervalMinutes;
  return t('ops.health.intervalRange', { factory: x.factory, min: x.min ?? 0, max: x.max ?? 0 });
});
</script>

<template>
  <q-card flat bordered class="full-height" :data-testid="`health-card-${check.code}`">
    <q-card-section class="row no-wrap items-start q-gutter-sm">
      <q-icon :name="look.icon" :color="look.color" size="sm" />
      <div class="col" style="min-width: 0">
        <div class="text-subtitle2" style="overflow-wrap: anywhere">{{ checkName(t, te, check.code) }}</div>
        <div :data-testid="`health-status-${check.code}`">
          {{ statusText }}<span v-if="s.value !== undefined">：{{ fmt(s.value) }}</span>
          <span v-if="messageText" class="text-muted">（{{ messageText }}）</span>
          <q-badge v-if="s.fromMemory" color="info" class="q-ml-xs">{{ t('ops.health.fromMemory') }}</q-badge>
        </div>
        <div class="text-caption text-muted">
          {{ t('ops.health.last') }} {{ formatTime(s.checkedUtc) }}<template v-if="s.nextDueUtc"> · {{ t('ops.health.next') }} {{ formatTime(s.nextDueUtc) }}</template>
        </div>
      </div>
      <q-btn
        v-if="check.applicable" flat dense no-caps color="primary" icon="play_arrow" :label="$q.screen.lt.sm ? undefined : t('ops.health.runNow')"
        :aria-label="t('ops.health.runNow')" :loading="run.isPending.value" :data-testid="`health-run-${check.code}`" @click="runNow"
      />
    </q-card-section>
    <q-card-section v-if="s.instances && s.instances.length > 1" class="q-pt-none" :data-testid="`health-instances-${check.code}`">
      <div class="text-caption text-muted">{{ t('ops.health.instances') }}</div>
      <div v-for="i in s.instances" :key="i.instanceId" class="text-caption" style="overflow-wrap: anywhere">
        {{ i.instanceId }}：{{ labelOr(t, te, `ops.healthStatus.${i.status}`, i.status) }}<template v-if="i.value !== undefined">（{{ fmt(i.value) }}）</template>
        · {{ formatTime(i.checkedUtc) }}
      </div>
    </q-card-section>
    <q-expansion-item v-if="check.applicable" v-model="expanded" dense :label="t('ops.health.more')" :data-testid="`health-expand-${check.code}`">
      <q-tabs v-model="tab" dense align="left" class="q-px-sm">
        <q-tab name="trend" :label="t('ops.health.trend')" />
        <q-tab name="settings" :label="t('ops.health.settings')" :data-testid="`health-settings-tab-${check.code}`" />
      </q-tabs>
      <div v-if="tab === 'trend'" class="q-pa-sm">
        <q-btn-toggle
          v-model="range" dense no-caps unelevated toggle-color="primary" class="q-mb-sm"
          :options="[{ value: 24, label: t('ops.health.last24h') }, { value: 168, label: t('ops.health.last7d') }]"
        />
        <div v-if="!points.length" class="text-muted">{{ results.isFetching.value ? t('designer.loading') : t('ops.health.noData') }}</div>
        <HealthTrendChart v-else :points="points" :format="fmt" :label="checkName(t, te, check.code)" />
        <div v-if="results.data.value?.truncated" class="text-caption text-muted">{{ t('ops.health.truncated') }}</div>
      </div>
      <div v-else class="q-pa-sm column no-wrap q-gutter-y-sm">
        <q-toggle v-if="check.canDisable" v-model="draft.enabled" :label="t('ops.health.enabled')" :data-testid="`health-enabled-${check.code}`" />
        <q-input
          v-model="draft.interval" type="number" dense outlined :label="t('ops.health.interval')" :placeholder="String(check.intervalMinutes.factory)"
          :hint="intervalHint" :error="!!err('intervalMinutes')" :error-message="err('intervalMinutes')" :data-testid="`health-interval-${check.code}`"
        />
        <q-input
          v-if="check.warn" v-model="draft.warn" type="number" dense outlined :label="t(check.direction === 'passFail' ? 'ops.health.busyStreak' : 'ops.health.warn')"
          :placeholder="String(check.warn.factory)" :hint="factoryHint(check.warn, draft.warn)" :error="!!err('warn')" :error-message="err('warn')"
          :data-testid="`health-warn-${check.code}`"
        />
        <q-input
          v-if="check.critical" v-model="draft.critical" type="number" dense outlined :label="t('ops.health.critical')"
          :placeholder="String(check.critical.factory)" :hint="factoryHint(check.critical, draft.critical)" :error="!!err('critical')"
          :error-message="err('critical')" :data-testid="`health-critical-${check.code}`"
        />
        <div class="text-caption text-muted">{{ t('ops.health.blankIsFactory') }}</div>
        <div class="row q-gutter-sm">
          <q-btn color="primary" dense no-caps :label="t('ops.save')" :disable="invalid" :loading="update.isPending.value" :data-testid="`health-save-${check.code}`" @click="save" />
          <q-btn flat dense no-caps :label="t('ops.cancel')" @click="reset" />
        </div>
      </div>
    </q-expansion-item>
  </q-card>
</template>
