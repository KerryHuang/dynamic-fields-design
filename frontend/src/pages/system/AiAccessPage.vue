<script setup lang="ts">
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRoute, useRouter } from 'vue-router';
import {
  useAdminMcpTokens, useAdminRevokeAllMcpTokens, useAdminRevokeMcpToken,
} from 'src/application/dynamic/queries/mcp-queries';
import { useMe, useUsers } from 'src/application/dynamic/queries/permissions-queries';
import ForbiddenNotice from 'src/components/framework/ForbiddenNotice.vue';
import type { McpToken } from 'src/domain/mcp/mcp-types';
import { formatTime } from 'src/domain/ops/ops-format';
import McpConfirmDialog from 'src/presentation/mcp/McpConfirmDialog.vue';
import { scopeText, tokenStatusText } from 'src/presentation/mcp/token-text';

// 管理員「AI 連線」頁（MCP 設計 §13.5）：權杖（全部、依使用者與狀態篩選、單筆與整人撤銷，皆二次確認）與說明兩個頁籤。
// 開關與上限在政策頁；呼叫紀錄不另做頁面，連到稽核頁帶來源管道＝MCP。頁籤用網址 `tab` 記住。
const { t, te } = useI18n();
const route = useRoute();
const router = useRouter();
const me = useMe();
const isAdmin = computed(() => me.data.value?.isSystemAdmin === true);
const users = useUsers();

const TABS = ['tokens', 'help'] as const;
type Tab = (typeof TABS)[number];
const tab = computed<Tab>({
  get: () => ((TABS as readonly string[]).includes(String(route.query.tab)) ? (route.query.tab as Tab) : 'tokens'),
  set: (v) => { void router.replace({ query: { ...route.query, tab: v } }); },
});

const filter = ref<{ userId: string | null; status: string | null }>({ userId: null, status: null });
const apiFilter = computed(() => ({
  ...(filter.value.userId ? { userId: filter.value.userId } : {}),
  ...(filter.value.status ? { status: filter.value.status } : {}),
}));
const tokens = useAdminMcpTokens(apiFilter, isAdmin);
const userOptions = computed(() => (users.data.value ?? []).map((u) => ({ value: u.userId, label: `${u.displayName || u.userId}（${u.userId}）` })));
const statusOptions = computed(() => ['active', 'expired', 'revoked'].map((s) => ({ value: s, label: t(`aiAccess.statusOption.${s}`) })));
const userName = (id: string) => users.data.value?.find((u) => u.userId === id)?.displayName || id;

const revokeOne = useAdminRevokeMcpToken();
const revokeAll = useAdminRevokeAllMcpTokens();
const oneTarget = ref<McpToken | null>(null);
const allTarget = ref<McpToken | null>(null);
const revokedCount = ref<number | null>(null);
const failed = ref(false);

function confirmOne() {
  const k = oneTarget.value;
  if (!k) return;
  failed.value = false;
  revokedCount.value = null;
  revokeOne.mutate(k.id, {
    onSuccess: () => { oneTarget.value = null; revokedCount.value = 1; },
    onError: () => { oneTarget.value = null; failed.value = true; },
  });
}
function confirmAll() {
  const k = allTarget.value;
  if (!k) return;
  failed.value = false;
  revokedCount.value = null;
  revokeAll.mutate(k.userId, {
    onSuccess: (r) => { allTarget.value = null; revokedCount.value = r.revoked; },
    onError: () => { allTarget.value = null; failed.value = true; },
  });
}
</script>

<template>
  <q-page padding>
    <ForbiddenNotice v-if="me.data.value && !isAdmin" />
    <template v-else-if="isAdmin">
      <div class="text-h6 q-mb-sm">{{ t('aiAccess.title') }}</div>
      <q-tabs v-model="tab" dense align="left" class="q-mb-md" data-testid="ai-admin-tabs">
        <q-tab v-for="name in TABS" :key="name" :name="name" :label="t(`aiAccess.tabs.${name}`)" :data-testid="`ai-admin-tab-${name}`" />
      </q-tabs>
      <q-tab-panels v-model="tab" animated>
        <q-tab-panel name="tokens" class="q-pa-none">
          <div class="row q-col-gutter-sm q-mb-md">
            <div class="col-12 col-sm-6 col-md-4">
              <q-select
                v-model="filter.userId" :options="userOptions" clearable emit-value map-options dense outlined :label="t('aiAccess.filters.user')"
                data-testid="ai-admin-filter-user"
              />
            </div>
            <div class="col-12 col-sm-6 col-md-4">
              <q-select
                v-model="filter.status" :options="statusOptions" clearable emit-value map-options dense outlined :label="t('aiAccess.filters.status')"
                data-testid="ai-admin-filter-status"
              />
            </div>
          </div>
          <div v-if="revokedCount !== null" class="text-positive q-mb-sm" data-testid="ai-admin-revoked">{{ t('aiAccess.revoked', { count: revokedCount }) }}</div>
          <div v-if="failed" class="text-negative q-mb-sm" data-testid="ai-admin-revoke-error">{{ t('aiAccess.revokeFailed') }}</div>
          <div v-if="tokens.isError.value" class="text-negative">{{ t('aiAccess.loadFailed') }}</div>
          <div v-else-if="tokens.data.value && tokens.data.value.length === 0" class="text-muted" data-testid="ai-admin-empty">{{ t('aiAccess.empty') }}</div>
          <div class="row q-col-gutter-md">
            <div v-for="k in tokens.data.value ?? []" :key="k.id" class="col-12 col-lg-6">
              <q-card flat bordered :data-testid="`ai-admin-token-${k.id}`">
                <q-card-section>
                  <div class="row items-center no-wrap">
                    <div class="col" style="overflow-wrap: anywhere">
                      <span class="text-muted q-mr-xs" data-testid="ai-token-id">#{{ k.id }}</span>
                      <span class="text-weight-medium">{{ k.name }}</span>
                      <span class="text-muted"> · {{ userName(k.userId) }}（{{ k.userId }}）</span>
                    </div>
                    <q-badge color="primary" :outline="k.status !== 'active'" class="q-ml-sm">{{ tokenStatusText(t, te, k) }}</q-badge>
                  </div>
                  <dl class="ai-fields q-mt-sm">
                    <dt>{{ t('aiTokens.cols.scopes') }}</dt>
                    <dd><div v-for="s in k.scopes" :key="s">{{ scopeText(t, te, s) }}</div></dd>
                    <dt>{{ t('aiTokens.cols.created') }}</dt>
                    <dd>{{ formatTime(k.createdUtc) }}</dd>
                    <dt>{{ t('aiTokens.cols.expires') }}</dt>
                    <dd>{{ k.expiresOn }}</dd>
                    <dt>{{ t('aiTokens.cols.lastUsed') }}</dt>
                    <dd>{{ k.lastUsedUtc ? formatTime(k.lastUsedUtc) : t('aiTokens.neverUsed') }}</dd>
                    <dt>{{ t('aiTokens.cols.todayCalls') }}</dt>
                    <dd>{{ k.todayCalls }}</dd>
                  </dl>
                </q-card-section>
                <q-card-actions v-if="k.status === 'active'">
                  <q-btn flat dense no-caps color="negative" icon="block" :label="t('aiAccess.revoke')" :data-testid="`ai-admin-revoke-${k.id}`" @click="oneTarget = k" />
                  <q-btn
                    flat dense no-caps color="negative" icon="person_off" :label="t('aiAccess.revokeAll')" :data-testid="`ai-admin-revoke-all-${k.id}`"
                    @click="allTarget = k"
                  />
                </q-card-actions>
              </q-card>
            </div>
          </div>
        </q-tab-panel>

        <q-tab-panel name="help" class="q-pa-none">
          <div data-testid="ai-admin-help" class="ai-help">
            <p class="text-muted">{{ t('aiAccess.help.intro') }}</p>
            <section v-for="s in ['data', 'groups', 'fields']" :key="s" class="q-mb-md">
              <div class="text-subtitle1 text-weight-medium">{{ t(`aiAccess.help.${s}Title`) }}</div>
              <div>{{ t(`aiAccess.help.${s}Body`) }}</div>
            </section>
            <section class="q-mb-md">
              <div class="text-subtitle1 text-weight-medium">{{ t('aiAccess.help.policyTitle') }}</div>
              <div>{{ t('aiAccess.help.policyBody') }}</div>
              <router-link to="/system/policies" data-testid="ai-admin-policy-link">{{ t('aiAccess.help.policyLink') }}</router-link>
            </section>
            <section class="q-mb-md">
              <div class="text-subtitle1 text-weight-medium">{{ t('aiAccess.help.auditTitle') }}</div>
              <div>{{ t('aiAccess.help.auditBody') }}</div>
              <router-link :to="{ path: '/system/audit', query: { channel: 'mcp' } }" data-testid="ai-admin-audit-link">{{ t('aiAccess.help.auditLink') }}</router-link>
            </section>
          </div>
        </q-tab-panel>
      </q-tab-panels>

      <McpConfirmDialog
        :model-value="oneTarget !== null" :title="t('aiAccess.revokeTitle', { user: oneTarget ? userName(oneTarget.userId) : '', name: oneTarget?.name ?? '' })"
        :body="t('aiAccess.revokeBody')" :confirm-label="t('aiAccess.revoke')" :loading="revokeOne.isPending.value" testid="ai-admin-revoke-confirm"
        @update:model-value="(v) => { if (!v) oneTarget = null; }" @confirm="confirmOne"
      />
      <McpConfirmDialog
        :model-value="allTarget !== null" :title="t('aiAccess.revokeAllTitle', { user: allTarget ? userName(allTarget.userId) : '' })"
        :body="t('aiAccess.revokeAllBody')" :confirm-label="t('aiAccess.revokeAll')" :loading="revokeAll.isPending.value" testid="ai-admin-revoke-all-confirm"
        @update:model-value="(v) => { if (!v) allTarget = null; }" @confirm="confirmAll"
      />
    </template>
  </q-page>
</template>

<style scoped>
.ai-fields { display: grid; grid-template-columns: max-content 1fr; gap: 2px 12px; margin: 0; }
.ai-fields dt { color: var(--t-muted); }
.ai-fields dd { margin: 0; overflow-wrap: anywhere; }
.ai-help { max-width: 720px; }
</style>
