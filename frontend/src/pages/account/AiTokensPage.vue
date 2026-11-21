<script setup lang="ts">
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useMcpCalls, useMyMcp, useMyMcpTokens, useRevokeMyMcpToken } from 'src/application/dynamic/queries/mcp-queries';
import type { McpToken } from 'src/domain/mcp/mcp-types';
import { formatTime, labelOr } from 'src/domain/ops/ops-format';
import CreateAiTokenDialog from 'src/presentation/mcp/CreateAiTokenDialog.vue';
import McpConfirmDialog from 'src/presentation/mcp/McpConfirmDialog.vue';
import { scopeText, tokenStatusText } from 'src/presentation/mcp/token-text';

// 個人「AI 存取權杖」頁（MCP 設計 §13.5）：能不能建立由 me/mcp 的 canUse 決定；不能用時顯示原因，
// 既有權杖照樣列出、可撤銷（後端只擋建立）。窄螢幕是卡片，不用表格。
const { t, te } = useI18n();
const info = useMyMcp();
const tokens = useMyMcpTokens();
const revoke = useRevokeMyMcpToken();

const reason = computed(() => info.data.value?.reason ?? null);
const createOpen = ref(false);
const expanded = ref<number | null>(null);
const calls = useMcpCalls(expanded);
const toRevoke = ref<McpToken | null>(null);
const revokeError = ref(false);

const confirmRevoke = () => {
  const target = toRevoke.value;
  if (!target) return;
  revokeError.value = false;
  revoke.mutate(target.id, {
    onSuccess: () => { toRevoke.value = null; },
    onError: () => { toRevoke.value = null; revokeError.value = true; },
  });
};
const toggle = (id: number) => { expanded.value = expanded.value === id ? null : id; };
const callText = (c: { action: string; tool?: string | null }) => c.tool || labelOr(t, te, `audit.actions.${c.action}`, c.action);
</script>

<template>
  <q-page padding>
    <div class="row items-center q-mb-sm">
      <div class="text-h6 col">{{ t('aiTokens.title') }}</div>
      <q-btn
        color="primary" no-caps icon="add" :label="t('aiTokens.create')" :disable="!info.data.value?.canUse" data-testid="ai-create"
        @click="createOpen = true"
      />
    </div>
    <div class="text-muted q-mb-md">{{ t('aiTokens.intro') }}</div>
    <q-banner v-if="reason" dense class="bg-info-soft q-mb-md" data-testid="ai-reason">{{ labelOr(t, te, `aiTokens.reasons.${reason}`, t('aiTokens.reasons.other')) }}</q-banner>
    <div v-if="info.isError.value" class="text-negative q-mb-sm" data-testid="ai-info-error">{{ t('aiTokens.infoFailed') }}</div>
    <div v-if="revokeError" class="text-negative q-mb-sm" data-testid="ai-revoke-error">{{ t('aiTokens.revokeFailed') }}</div>
    <div v-if="tokens.isError.value" class="text-negative" data-testid="ai-load-error">{{ t('aiTokens.loadFailed') }}</div>
    <div v-else-if="tokens.data.value && tokens.data.value.length === 0" class="text-muted" data-testid="ai-empty">{{ t('aiTokens.empty') }}</div>

    <div class="row q-col-gutter-md">
      <div v-for="k in tokens.data.value ?? []" :key="k.id" class="col-12 col-lg-6">
        <q-card flat bordered :data-testid="`ai-token-${k.id}`">
          <q-card-section>
            <div class="row items-center no-wrap">
              <div class="text-weight-medium col" style="overflow-wrap: anywhere">
                <span class="text-muted q-mr-xs" data-testid="ai-token-id">#{{ k.id }}</span>{{ k.name }}
              </div>
              <q-badge color="primary" :outline="k.status !== 'active'" class="q-ml-sm" data-testid="ai-status">{{ tokenStatusText(t, te, k) }}</q-badge>
            </div>
            <div class="text-caption text-muted">{{ t('aiTokens.last4', { last4: k.last4 }) }}</div>
            <dl class="ai-fields q-mt-sm">
              <dt>{{ t('aiTokens.cols.scopes') }}</dt>
              <dd>
                <div v-for="s in k.scopes" :key="s">{{ scopeText(t, te, s) }}</div>
              </dd>
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
          <q-card-actions>
            <q-btn
              flat dense no-caps :icon="expanded === k.id ? 'expand_less' : 'expand_more'" :label="expanded === k.id ? t('aiTokens.hideCalls') : t('aiTokens.calls')"
              :data-testid="`ai-expand-${k.id}`" @click="toggle(k.id)"
            />
            <q-space />
            <q-btn
              v-if="k.status === 'active'" flat dense no-caps color="negative" icon="block" :label="t('aiTokens.revoke')"
              :data-testid="`ai-revoke-${k.id}`" @click="toRevoke = k"
            />
          </q-card-actions>
          <q-card-section v-if="expanded === k.id" class="q-pt-none" :data-testid="`ai-calls-${k.id}`">
            <div v-if="calls.isError.value" class="text-negative">{{ t('aiTokens.callsFailed') }}</div>
            <div v-else-if="calls.data.value && calls.data.value.length === 0" class="text-muted">{{ t('aiTokens.noCalls') }}</div>
            <div v-for="c in calls.data.value ?? []" :key="c.id" class="text-caption" style="overflow-wrap: anywhere">
              {{ formatTime(c.occurredUtc) }} · {{ callText(c) }} · {{ labelOr(t, te, `audit.result.${c.result}`, c.result) }}
            </div>
          </q-card-section>
        </q-card>
      </div>
    </div>

    <CreateAiTokenDialog v-if="info.data.value" v-model="createOpen" :info="info.data.value" />
    <McpConfirmDialog
      :model-value="toRevoke !== null" :title="t('aiTokens.revokeTitle', { name: toRevoke?.name ?? '' })" :body="t('aiTokens.revokeBody')"
      :confirm-label="t('aiTokens.revoke')" :loading="revoke.isPending.value" testid="ai-revoke-confirm"
      @update:model-value="(v) => { if (!v) toRevoke = null; }" @confirm="confirmRevoke"
    />
  </q-page>
</template>

<style scoped>
.ai-fields { display: grid; grid-template-columns: max-content 1fr; gap: 2px 12px; margin: 0; }
.ai-fields dt { color: var(--t-muted); }
.ai-fields dd { margin: 0; overflow-wrap: anywhere; }
</style>
