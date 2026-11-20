<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import type { LocaleAdminEntry, LocaleReviewSection } from 'src/domain/dynamic/locale-pack-types';
import { formatTime } from 'src/domain/ops/ops-format';

// 語系卡片（語系包設計 §6.5；i4d2）：完整度與覆寫檢視都是後端算好的，這裡只呈現；390 寬同一套版型，操作鈕放卡片下方。
const props = defineProps<{ locale: LocaleAdminEntry; importEnabled: boolean; previewing: string | null }>();
const emit = defineEmits<{
  exportTemplate: [];
  preview: [];
  action: [action: 'rollback' | 'clear' | 'remove'];
}>();
const { t } = useI18n();

const SECTIONS = ['ui', 'server'] as const;
const percent = (have: number, total: number) => (total > 0 ? Math.round((have / total) * 100) : 0);
const sum = (k: 'baseChanged' | 'paramsChanged' | 'runtimeFallback' | 'orphaned') => props.locale.review.ui[k] + props.locale.review.server[k];
const hasOverrides = computed(() => props.locale.overrides.ui + props.locale.overrides.server > 0);
const canPreview = computed(() => !props.locale.enabled && props.previewing !== props.locale.code);

const versionText = computed(() => {
  const c = props.locale.current;
  if (!c) return '';
  const time = formatTime(c.importedUtc);
  const version = c.packVersion ?? c.fileName;
  return version ? t('localePacks.currentVersion', { version, time }) : t('localePacks.currentEmpty', { time });
});

type Kind = { count: 'baseChanged' | 'paramsChanged' | 'runtimeFallback' | 'orphaned'; keys: keyof LocaleReviewSection; title: string };
const KINDS: Kind[] = [
  { count: 'baseChanged', keys: 'baseChangedKeys', title: 'localePacks.review.keysBaseChanged' },
  { count: 'paramsChanged', keys: 'paramsChangedKeys', title: 'localePacks.review.keysParams' },
  { count: 'runtimeFallback', keys: 'runtimeFallbackKeys', title: 'localePacks.review.keysFallback' },
  { count: 'orphaned', keys: 'orphanedKeys', title: 'localePacks.review.keysOrphaned' },
];
// 每種鍵清單按段分組；後端只回前 50 筆，超過時註明總數
const groups = computed(() => KINDS.flatMap((k) => SECTIONS
  .filter((s) => props.locale.review[s][k.count] > 0)
  .map((s) => ({
    id: `${k.count}-${s}`, title: t(k.title), section: t(`localePacks.report.sections.${s}`),
    keys: props.locale.review[s][k.keys] as string[], total: props.locale.review[s][k.count],
  }))));
const topMissing = computed(() => props.locale.completeness.topMissing.map((m) => t('localePacks.card.missingRow', {
  section: t(`localePacks.report.sections.${m.segment}`), category: m.category, n: m.missing,
})));
</script>

<template>
  <q-card flat bordered class="column no-wrap full-height" :data-testid="`locale-row-${locale.code}`">
    <q-card-section class="col">
      <div style="overflow-wrap: anywhere">
        <span class="text-weight-bold">{{ locale.nativeName }}</span>
        <span class="text-caption text-muted q-ml-sm">{{ locale.code }}</span>
        <q-badge outline class="q-ml-sm">{{ t(`localePacks.origin.${locale.origin}`) }}</q-badge>
        <q-badge v-if="locale.enabled" color="positive" class="q-ml-xs">{{ t('localePacks.enabled') }}</q-badge>
        <q-badge v-if="locale.isDefault" color="primary" class="q-ml-xs">{{ t('localePacks.default') }}</q-badge>
      </div>

      <div v-for="s in SECTIONS" :key="s" class="q-mt-sm" :data-testid="`locale-completeness-${s}-${locale.code}`">
        <div class="text-caption">
          {{ t(s === 'ui' ? 'localePacks.card.completenessUi' : 'localePacks.card.completenessServer', {
            have: locale.completeness[s].have, total: locale.completeness[s].total,
            pct: percent(locale.completeness[s].have, locale.completeness[s].total),
          }) }}
        </div>
        <q-linear-progress rounded size="6px" color="primary" track-color="grey-4" aria-hidden="true"
          :value="locale.completeness[s].total > 0 ? locale.completeness[s].have / locale.completeness[s].total : 0" />
      </div>

      <div v-if="hasOverrides" class="text-caption text-muted q-mt-sm">
        {{ t('localePacks.overrides', { ui: locale.overrides.ui, server: locale.overrides.server }) }}
      </div>
      <div v-if="locale.current" class="text-caption text-muted" style="overflow-wrap: anywhere" :data-testid="`locale-version-${locale.code}`">{{ versionText }}</div>
      <div v-if="locale.builtFor" class="text-caption text-muted" style="overflow-wrap: anywhere" :data-testid="`locale-built-for-${locale.code}`">
        {{ t('localePacks.card.builtFor', { version: locale.builtFor }) }}
      </div>

      <div v-if="sum('baseChanged') > 0" class="text-caption text-warning q-mt-sm" :data-testid="`locale-review-base-changed-${locale.code}`">
        {{ t('localePacks.review.baseChanged', { n: sum('baseChanged') }) }}
      </div>
      <div v-if="sum('paramsChanged') > 0" class="text-caption text-warning" :data-testid="`locale-review-params-${locale.code}`">
        {{ sum('runtimeFallback') > 0
          ? t('localePacks.review.paramsMismatchFallback', { n: sum('paramsChanged'), m: sum('runtimeFallback') })
          : t('localePacks.review.paramsMismatch', { n: sum('paramsChanged') }) }}
      </div>
      <div v-if="sum('orphaned') > 0" class="text-caption text-warning" :data-testid="`locale-review-orphaned-${locale.code}`">
        {{ t('localePacks.review.orphaned', { n: sum('orphaned') }) }}
      </div>

      <q-expansion-item v-if="topMissing.length || groups.length" dense class="q-mt-sm" :label="t('localePacks.card.details')" :data-testid="`locale-details-${locale.code}`">
        <div v-if="topMissing.length" class="text-caption q-mb-sm">
          <div class="text-weight-medium">{{ t('localePacks.card.topMissing') }}</div>
          <div v-for="m in topMissing" :key="m" style="overflow-wrap: anywhere">{{ m }}</div>
        </div>
        <div v-for="g in groups" :key="g.id" class="text-caption q-mb-sm">
          <div class="text-weight-medium">{{ g.title }}・{{ g.section }}</div>
          <div v-if="g.total > g.keys.length" class="text-muted">{{ t('localePacks.review.keysMore', { total: g.total }) }}</div>
          <ul class="locale-keys">
            <li v-for="k in g.keys" :key="k"><code>{{ k }}</code></li>
          </ul>
        </div>
      </q-expansion-item>

      <div v-if="canPreview" class="text-caption text-muted q-mt-sm">{{ t('localePacks.preview.hint') }}</div>
    </q-card-section>

    <q-card-actions>
      <q-btn flat dense no-caps icon="download" :label="t('localePacks.card.exportTemplate')" :data-testid="`locale-export-${locale.code}`" @click="emit('exportTemplate')" />
      <q-btn v-if="canPreview" flat dense no-caps icon="visibility" :label="t('localePacks.preview.action')" :data-testid="`locale-preview-${locale.code}`" @click="emit('preview')" />
      <q-space />
      <q-btn flat round dense icon="more_horiz" :aria-label="t('common.moreActions')" :data-testid="`locale-actions-${locale.code}`">
        <q-menu>
          <q-list dense style="min-width: 200px">
            <q-item v-if="locale.hasPrevious" v-close-popup clickable :disable="!importEnabled" :data-testid="`locale-rollback-${locale.code}`" @click="emit('action', 'rollback')">
              <q-item-section>{{ t('localePacks.actions.rollback') }}</q-item-section>
            </q-item>
            <q-item v-if="hasOverrides" v-close-popup clickable :disable="!importEnabled" :data-testid="`locale-clear-${locale.code}`" @click="emit('action', 'clear')">
              <q-item-section>{{ t('localePacks.actions.clear') }}</q-item-section>
            </q-item>
            <q-item v-if="locale.origin === 'installed'" v-close-popup clickable :disable="!importEnabled" :data-testid="`locale-remove-${locale.code}`" @click="emit('action', 'remove')">
              <q-item-section>{{ t('localePacks.actions.remove') }}</q-item-section>
            </q-item>
            <q-item v-if="!importEnabled" dense>
              <q-item-section class="text-caption text-muted">{{ t('localePacks.importDisabledItem') }}</q-item-section>
            </q-item>
          </q-list>
        </q-menu>
      </q-btn>
    </q-card-actions>
  </q-card>
</template>

<style scoped>
.locale-keys { margin: 2px 0 0; padding-left: 20px; }
.locale-keys li { overflow-wrap: anywhere; }
</style>
