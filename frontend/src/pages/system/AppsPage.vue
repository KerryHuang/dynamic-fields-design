<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import {
  useAppPackages, useApps, useCloseApp, useImportAppPackage, useOpenApp, usePreviewOpenApp, useReopenApp,
} from 'src/application/dynamic/queries/apps-queries';
import { useMe } from 'src/application/dynamic/queries/permissions-queries';
import ForbiddenNotice from 'src/components/framework/ForbiddenNotice.vue';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import { notifyApiError } from 'src/presentation/dynamic/designer/api-error';
import type { AppInfo, AppPackageSummary, OpenAppBody, OpenPreview } from 'src/services/api/dynamic-api';
import { isForbidden, type ApiError } from 'src/services/api/http';
import { joinList } from 'src/i18n/translate';

const { t, locale } = useI18n();
const $q = useQuasar();
const me = useMe();
const packages = useAppPackages();
const apps = useApps();
const importPackage = useImportAppPackage();
const preview = usePreviewOpenApp();
const openApp = useOpenApp();
const closeApp = useCloseApp();
const reopenApp = useReopenApp();

const forbidden = computed(() => isForbidden(packages.error.value) || isForbidden(apps.error.value));
const canWrite = computed(() => !!me.data.value?.designer.add && !!me.data.value?.designer.edit);
const appByKey = computed(() => new Map((apps.data.value ?? []).map((a) => [a.key, a])));
const name = (n: Parameters<typeof resolveI18n>[0]) => resolveI18n(n, locale.value);
const statusColor = (s: AppPackageSummary['status']) => (s === 'Open' ? 'positive' : 'secondary');
const fail = (e: unknown, fallback: string) => notifyApiError((o) => $q.notify(o), e as ApiError, [], fallback);

// ── 匯入 ──
const fileInput = ref<HTMLInputElement | null>(null);
async function onFile(ev: Event) {
  const input = ev.target as HTMLInputElement;
  const file = input.files?.[0];
  input.value = '';
  if (!file) return;
  importPackage.mutate(await file.text(), {
    onSuccess: (r) => {
      $q.notify({ type: 'positive', message: t(r.merged ? 'apps.merged' : 'apps.imported') });
      (r.warnings ?? []).forEach((w) => $q.notify({ type: 'warning', message: w, timeout: 0, closeBtn: true }));
    },
    onError: (e) => fail(e, t('apps.importFailed')),
  });
}

// ── 開啟 ──
const opening = ref<AppPackageSummary | null>(null);
const result = ref<OpenPreview | null>(null);
// 預設照伺服器建議：找得到同名群組就對應，否則建立
const groupMapOf = (groups: OpenPreview['groups']): OpenAppBody['groupMap'] => Object.fromEntries(groups.map((g) => [
  g.code, g.suggestion === 'map' ? { mode: 'map' as const, groupId: g.suggestedGroupId } : { mode: g.suggestion },
]));
// 一起開啟的共用主檔各有自己的群組（如 steward），同樣照建議對應；不列就會被略過、沒人有主檔權限
const mastersOf = (masters: OpenPreview['masters']): OpenAppBody['masters'] => {
  const opened = (masters ?? []).filter((m) => m.action === 'open' && m.groups.length);
  return opened.length ? Object.fromEntries(opened.map((m) => [m.id, groupMapOf(m.groups)!])) : undefined;
};
const bodyOf = (r: OpenPreview): OpenAppBody => {
  const masters = mastersOf(r.masters);
  return { groupMap: groupMapOf(r.groups), ...(masters ? { masters } : {}), locale: locale.value };
};
const openBody = computed<OpenAppBody>(() => (result.value ? bodyOf(result.value) : { groupMap: {}, locale: locale.value }));
const masterGroups = computed(() => (result.value?.masters ?? []).filter((m) => m.action === 'open' && m.groups.length));

function startOpen(p: AppPackageSummary) {
  opening.value = p;
  result.value = null;
  const failed = (e: unknown) => { opening.value = null; fail(e, t('apps.previewFailed')); };
  // 第一次不帶對應只為取得建議；警告要以「套用建議後」的結果為準，所以再預覽一次
  preview.mutate({ id: p.id, body: { locale: locale.value } }, {
    onError: failed,
    onSuccess: (first) => preview.mutate(
      { id: p.id, body: bodyOf(first) },
      { onSuccess: (r) => { result.value = r; }, onError: failed }),
  });
}
const blockers = computed(() => {
  const r = result.value;
  if (!r) return [];
  return [
    ...r.errors,
    ...r.keyConflicts.map((c) => t('apps.keyConflict', { module: c.moduleCode, key: c.key })),
    ...r.hostEntityConflicts,
    ...r.missingDependencies.map((m) => t('apps.missing', { app: m.appId ?? '', detail: m.moduleCode ?? m.entity ?? '', reason: m.reason })),
    ...(r.unbound ?? []).map((u) => t('apps.unbound', { detail: u })),
  ];
});
function confirmOpen() {
  const p = opening.value;
  if (!p) return;
  openApp.mutate({ id: p.id, body: openBody.value }, {
    onSuccess: () => { opening.value = null; $q.notify({ type: 'positive', message: t('apps.opened') }); },
    onError: (e) => fail(e, t('apps.openFailed')),
  });
}

// ── 關閉／重新開啟 ──
const closing = ref<AppInfo | null>(null);
function doClose(cascade: boolean) {
  const a = closing.value;
  if (!a) return;
  closeApp.mutate({ key: a.key, cascade }, {
    onSuccess: (r) => {
      closing.value = null;
      $q.notify({ type: 'positive', message: t('apps.closed', { n: r.closed.length }) });
      r.warnings.forEach((w) => $q.notify({ type: 'warning', message: w }));
    },
    onError: (e) => fail(e, t('apps.closeFailed')),
  });
}
const reopen = (key: string) => reopenApp.mutate(key, {
  onSuccess: () => $q.notify({ type: 'positive', message: t('apps.reopened') }),
  onError: (e) => fail(e, t('apps.reopenFailed')),
});
</script>

<template>
  <q-page padding>
    <ForbiddenNotice v-if="forbidden" />
    <template v-else>
      <div class="row items-center q-mb-md q-gutter-sm">
        <div class="text-h6">{{ t('apps.title') }}</div>
        <q-space />
        <input ref="fileInput" type="file" accept=".json,application/json" class="hidden" data-testid="apps-file" @change="onFile" />
        <q-btn v-if="canWrite" outline icon="upload" no-caps :label="t('apps.import')" data-testid="apps-import" @click="fileInput?.click()" />
      </div>
      <p class="text-muted">{{ t('apps.description') }}</p>
      <div v-if="packages.data.value && !packages.data.value.length" class="text-muted" data-testid="apps-empty">{{ t('apps.empty') }}</div>

      <q-list bordered separator>
        <q-item v-for="p in packages.data.value" :key="p.id" data-testid="app-row">
          <q-item-section avatar><q-icon :name="p.icon || 'inventory_2'" /></q-item-section>
          <q-item-section>
            <q-item-label>{{ name(p.name) }} <span class="text-caption text-muted">v{{ p.version }}</span></q-item-label>
            <q-item-label v-if="p.description" caption>{{ name(p.description) }}</q-item-label>
            <q-item-label caption>
              {{ t('apps.modules', { n: p.modules.length }) }}
              <template v-if="p.requires.apps.length">・{{ t('apps.requires', { apps: joinList(p.requires.apps.map((r) => r.app)) }) }}</template>
            </q-item-label>
            <q-item-label v-if="p.status && appByKey.get(p.id)?.dependents.length" caption>
              {{ t('apps.dependents', { apps: joinList(appByKey.get(p.id)!.dependents) }) }}
            </q-item-label>
          </q-item-section>
          <q-item-section side>
            <div class="row items-center q-gutter-sm no-wrap">
              <q-badge :color="statusColor(p.status)" :outline="!p.status" data-testid="app-status">
                {{ t(`apps.status.${p.status ?? 'none'}`) }}
              </q-badge>
              <q-btn v-if="canWrite && !p.status" color="primary" dense no-caps :label="t('apps.open')" data-testid="app-open" @click="startOpen(p)" />
              <q-btn v-if="canWrite && p.status === 'Open' && appByKey.get(p.id)" outline dense no-caps :label="t('apps.close')" data-testid="app-close" @click="closing = appByKey.get(p.id)!" />
              <q-btn v-if="canWrite && p.status === 'Closed'" color="primary" dense no-caps :label="t('apps.reopen')" data-testid="app-reopen" @click="reopen(p.id)" />
            </div>
          </q-item-section>
        </q-item>
      </q-list>

      <q-dialog :model-value="!!opening" @update:model-value="opening = null">
        <q-card style="min-width: 380px" data-testid="open-dialog">
          <q-card-section class="text-subtitle1">{{ t('apps.openTitle', { name: opening ? name(opening.name) : '' }) }}</q-card-section>
          <q-card-section v-if="!result"><q-spinner /></q-card-section>
          <q-card-section v-else>
            <div v-if="blockers.length" class="text-negative" data-testid="open-blockers">
              <div>{{ t('apps.cannotOpen') }}</div>
              <ul><li v-for="b in blockers" :key="b">{{ b }}</li></ul>
            </div>
            <template v-else>
              <p>{{ t('apps.openHint') }}</p>
              <ul v-if="result.groups.length" data-testid="open-groups">
                <li v-for="g in result.groups" :key="g.code">{{ name(g.name) }}：{{ t(`apps.group.${g.suggestion}`) }}</li>
              </ul>
              <div v-for="m in masterGroups" :key="m.id" data-testid="open-masters">
                <div class="text-caption text-muted">{{ t('apps.masterGroups', { name: name(m.name) }) }}</div>
                <ul><li v-for="g in m.groups" :key="g.code">{{ name(g.name) }}：{{ t(`apps.group.${g.suggestion}`) }}</li></ul>
              </div>
            </template>
            <ul v-if="result.warnings.length" class="text-muted"><li v-for="w in result.warnings" :key="w">{{ w }}</li></ul>
          </q-card-section>
          <q-card-actions align="right">
            <q-btn flat no-caps :label="t('dynamic.cancel')" @click="opening = null" />
            <q-btn color="primary" no-caps :label="t('apps.open')" :disable="!result?.canOpen" :loading="openApp.isPending.value" data-testid="open-confirm" @click="confirmOpen" />
          </q-card-actions>
        </q-card>
      </q-dialog>

      <q-dialog :model-value="!!closing" @update:model-value="closing = null">
        <q-card style="min-width: 380px" data-testid="close-dialog">
          <q-card-section class="text-subtitle1">{{ t('apps.closeTitle', { name: closing ? name(closing.name) : '' }) }}</q-card-section>
          <q-card-section>
            <p>{{ t('apps.closeHint') }}</p>
            <div v-if="closing?.dependents.length" class="text-warning" data-testid="close-dependents">
              {{ t('apps.closeDependents', { apps: joinList(closing.dependents) }) }}
            </div>
            <div v-if="closing?.unaffiliatedReferrers.length" class="text-muted" data-testid="close-referrers">
              {{ t('apps.closeReferrers', { modules: joinList(closing.unaffiliatedReferrers) }) }}
            </div>
          </q-card-section>
          <q-card-actions align="right">
            <q-btn flat no-caps :label="t('dynamic.cancel')" @click="closing = null" />
            <q-btn v-if="closing?.dependents.length" color="negative" no-caps :label="t('apps.closeCascade')" :loading="closeApp.isPending.value" data-testid="close-cascade" @click="doClose(true)" />
            <q-btn v-else color="negative" no-caps :label="t('apps.close')" :loading="closeApp.isPending.value" data-testid="close-confirm" @click="doClose(false)" />
          </q-card-actions>
        </q-card>
      </q-dialog>
    </template>
  </q-page>
</template>
