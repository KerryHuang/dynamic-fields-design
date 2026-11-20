<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import type { LocalePackReportData, LocalePackRejectedItem } from 'src/domain/dynamic/locale-pack-types';

// 預覽報告（語系包設計 §6.3）：計數用定義清單（dl），不用表格，390 寬不橫捲；拒收原因與警告是代碼，用 i18n 鍵組字。
const props = defineProps<{ report: LocalePackReportData }>();
const { t, te } = useI18n();

const COUNTS = ['added', 'changed', 'unchanged', 'skipped', 'sameAsBuiltin', 'extra', 'rejected'] as const;
const SECTIONS = ['ui', 'server'] as const;

const percent = (have: number, total: number) => (total > 0 ? Math.round((have / total) * 100) : 0);
const fieldList = (fields: string[]) => fields.map((f) => t(`localePacks.report.fields.${f}`)).join('、');

function reasonText(item: LocalePackRejectedItem): string {
  const [first, second] = item.args;
  if (item.reason === 'placeholder') {
    return item.args.length ? t('localePacks.reasons.placeholder', { allowed: item.args.join('、') }) : t('localePacks.reasons.placeholderNone');
  }
  if (item.reason === 'booleanCollision') {
    return second ? t('localePacks.reasons.booleanCollision', { word: first ?? '', locale: second }) : t('localePacks.reasons.booleanCollisionAlias', { word: first ?? '' });
  }
  return te(`localePacks.reasons.${item.reason}`) ? t(`localePacks.reasons.${item.reason}`) : t('localePacks.reasons.unknown', { code: item.reason });
}

function warningText(w: { code: string; args: string[] }): string {
  if (w.code === 'booleanCollisionExisting') return t('localePacks.warnings.booleanCollisionExisting', { word: w.args[0] ?? '', yes: w.args[1] ?? '', no: w.args[2] ?? '' });
  return t('localePacks.warnings.unknown', { code: w.code });
}

const rejectedSections = computed(() => SECTIONS.filter((s) => props.report[s].rejected > 0));
const extraSections = computed(() => SECTIONS.filter((s) => props.report[s].extra > 0));
</script>

<template>
  <div data-testid="pack-report" class="locale-pack-report">
    <div class="text-subtitle1" style="overflow-wrap: anywhere">
      {{ t(report.locale.isNew ? 'localePacks.report.installNew' : 'localePacks.report.update', { name: report.locale.nativeName, code: report.locale.code }) }}
    </div>
    <div v-if="report.pack.version" class="text-caption text-muted">{{ t('localePacks.report.packVersion', { version: report.pack.version }) }}</div>
    <div v-if="report.pack.builtFor" class="text-caption text-muted" style="overflow-wrap: anywhere" data-testid="report-built-for">
      {{ t('localePacks.report.builtFor', { version: report.pack.builtFor }) }}
    </div>
    <div v-if="report.locale.changes.length" class="text-caption" data-testid="report-changes">
      {{ t('localePacks.report.changes', { fields: fieldList(report.locale.changes) }) }}
    </div>
    <div v-if="report.locale.ignored.length" class="text-caption text-warning" data-testid="report-ignored">
      {{ t('localePacks.report.ignored', { fields: fieldList(report.locale.ignored) }) }}
    </div>

    <div v-for="s in SECTIONS" :key="s" class="q-mt-md">
      <div class="text-weight-medium">{{ t(`localePacks.report.sections.${s}`) }}</div>
      <dl class="report-counts">
        <div v-for="c in COUNTS" :key="c" :data-testid="`report-${s}-${c}`" class="report-count">
          <dt>{{ t(`localePacks.report.counts.${c}`) }}</dt>
          <dd>{{ report[s][c] }}</dd>
        </div>
      </dl>
    </div>

    <div class="q-mt-md">
      <div class="text-weight-medium">{{ t('localePacks.report.completeness') }}</div>
      <div v-for="s in SECTIONS" :key="s" :data-testid="`report-completeness-${s}`" class="text-body2">
        {{ t('localePacks.report.completenessValue', {
          label: t(`localePacks.report.sections.${s}`), have: report.completeness[s].have, total: report.completeness[s].total,
          pct: percent(report.completeness[s].have, report.completeness[s].total),
        }) }}
      </div>
    </div>

    <div v-if="rejectedSections.length" class="q-mt-md" data-testid="report-rejected">
      <div class="text-weight-medium">{{ t('localePacks.report.rejectedTitle') }}</div>
      <template v-for="s in rejectedSections" :key="s">
        <div class="text-caption text-muted">
          {{ t(`localePacks.report.sections.${s}`) }}
          <template v-if="report[s].rejected > report[s].rejectedItems.length">
            ・{{ t('localePacks.report.rejectedMore', { shown: report[s].rejectedItems.length, total: report[s].rejected }) }}
          </template>
        </div>
        <ul class="report-rejected">
          <li v-for="item in report[s].rejectedItems" :key="item.key">
            <code>{{ item.key }}</code>：{{ reasonText(item) }}
          </li>
        </ul>
      </template>
    </div>

    <div v-if="extraSections.length" class="q-mt-md" data-testid="report-extra">
      <div class="text-weight-medium">{{ t('localePacks.report.extraTitle') }}</div>
      <template v-for="s in extraSections" :key="s">
        <div class="text-caption text-muted">
          {{ t(`localePacks.report.sections.${s}`) }}
          <template v-if="report[s].extra > report[s].extraKeys.length">
            ・{{ t('localePacks.report.rejectedMore', { shown: report[s].extraKeys.length, total: report[s].extra }) }}
          </template>
        </div>
        <ul class="report-rejected">
          <li v-for="key in report[s].extraKeys" :key="key"><code>{{ key }}</code></li>
        </ul>
      </template>
    </div>

    <div v-if="report.warnings.length" class="q-mt-md text-warning" data-testid="report-warnings">
      <div class="text-weight-medium">{{ t('localePacks.report.warningsTitle') }}</div>
      <ul class="report-rejected"><li v-for="w in report.warnings" :key="w.code + w.args.join()">{{ warningText(w) }}</li></ul>
    </div>

    <div v-if="report.designData.count" class="q-mt-md text-muted" data-testid="report-design-data">
      {{ t('localePacks.report.designData', { n: report.designData.count }) }}
    </div>
  </div>
</template>

<style scoped>
.report-counts { display: flex; flex-wrap: wrap; gap: 4px 16px; margin: 4px 0 0; }
.report-count { display: flex; gap: 6px; }
.report-count dt { opacity: 0.7; }
.report-count dd { margin: 0; font-weight: 500; }
.report-rejected { margin: 2px 0 0; padding-left: 20px; }
.report-rejected li { overflow-wrap: anywhere; }
</style>
