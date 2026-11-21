<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useCreateMcpToken } from 'src/application/dynamic/queries/mcp-queries';
import DateSelect from 'src/components/framework/DateSelect.vue';
import type { McpInfo } from 'src/domain/mcp/mcp-types';
import { leftoverMessages } from 'src/presentation/dynamic/designer/api-error';
import type { ApiError } from 'src/services/api/http';
import { joinList } from 'src/i18n/translate';
import { scopeText } from './token-text';

// 建立權杖（MCP 設計 §13.5）：表單 → 「只顯示一次」畫面。預設到期日與上限都是 API 算好的（政策），前端不留常數；
// 範圍相依（寫入要連同讀取）由後端判斷、回 400；前端不預先擋，只在有提示文案的範圍下方顯示提示（aiTokens.scopeHints.<範圍>）。
// 明文只存在這個元件的 ref，關閉就清掉；密碼用 secret mutation 綁當次提交，不進 mutation 快取。
const props = defineProps<{ modelValue: boolean; info: McpInfo }>();
const emit = defineEmits<{ 'update:modelValue': [value: boolean] }>();
const { t, te } = useI18n();
const create = useCreateMcpToken();

const name = ref('');
const password = ref('');
const expiresOn = ref<string | null>(null);
const scopes = ref<string[]>([]);
const error = ref('');
const secret = ref<string | null>(null);
const confirmClose = ref(false);
const copyState = ref<'idle' | 'copied' | 'failed'>('idle');

watch(() => props.modelValue, (open) => {
  if (!open) return;
  name.value = '';
  password.value = '';
  error.value = '';
  secret.value = null;
  copyState.value = 'idle';
  expiresOn.value = props.info.defaultExpiresOn;
  scopes.value = [...props.info.defaultScopes];
}, { immediate: true });

function submit() {
  error.value = '';
  // 空值或日期只打一半（框架會回英文的格式錯誤）一律送 null，讓後端回「到期日須在 X 到 Y 之間」
  const date = expiresOn.value && /^\d{4}-\d{2}-\d{2}$/.test(expiresOn.value) ? expiresOn.value : null;
  create.send({ name: name.value, scopes: [...scopes.value], expiresOn: date }, { password: password.value }, {
    onSuccess: (r) => { secret.value = r.token; password.value = ''; },
    onError: (e) => { error.value = joinList(leftoverMessages(e as unknown as ApiError, [])) || t('aiTokens.dialog.createFailed'); },
  });
}

function toggleScope(scope: string, on: boolean) {
  scopes.value = on ? [...scopes.value, scope] : scopes.value.filter((s) => s !== scope);
}

async function copy() {
  try {
    await navigator.clipboard.writeText(secret.value ?? '');
    copyState.value = 'copied';
  } catch { copyState.value = 'failed'; }
}

function close() {
  secret.value = null;
  password.value = '';
  confirmClose.value = false;
  create.reset();
  emit('update:modelValue', false);
}
/** 對話框要求關閉（Esc）：已出現明文時不直接關，改問一次；表單畫面還沒有東西可失去，直接關。點背景不關（no-backdrop-dismiss）。 */
function onToggle(open: boolean) {
  if (open) return;
  if (secret.value !== null) confirmClose.value = true; else close();
}
const envExample = computed(() => `export FORMWRIGHT_TOKEN='${secret.value ?? ''}'`);
</script>

<template>
  <q-dialog :model-value="modelValue" no-backdrop-dismiss :maximized="$q.screen.lt.sm" @update:model-value="onToggle">
    <q-card :style="$q.screen.lt.sm ? undefined : 'width: 560px; max-width: 95vw'" data-testid="ai-dialog">
      <template v-if="secret === null">
        <q-card-section class="text-h6">{{ t('aiTokens.dialog.title') }}</q-card-section>
        <q-form @submit.prevent="submit">
          <q-card-section class="q-gutter-sm">
            <q-input v-model="name" :label="t('aiTokens.dialog.name')" :hint="t('aiTokens.dialog.nameHint')" dense outlined data-testid="ai-name" />
            <fieldset class="ai-fieldset" data-testid="ai-scopes">
              <legend class="text-caption">{{ t('aiTokens.dialog.scopes') }}</legend>
              <div v-for="s in info.scopes" :key="s" class="q-mb-xs">
                <q-checkbox
                  :model-value="scopes.includes(s)" dense :label="scopeText(t, te, s)" :data-testid="`ai-scope-${s}`"
                  @update:model-value="(v) => toggleScope(s, v)"
                />
                <div class="text-caption text-muted q-ml-lg" style="overflow-wrap: anywhere">{{ s }}</div>
                <div
                  v-if="te(`aiTokens.scopeHints.${s}`)" class="text-caption q-ml-lg" style="overflow-wrap: anywhere"
                  :data-testid="`ai-scope-hint-${s}`"
                >{{ t(`aiTokens.scopeHints.${s}`) }}</div>
              </div>
            </fieldset>
            <DateSelect v-model="expiresOn" :label="t('aiTokens.dialog.expiresOn')" :hint="t('aiTokens.dialog.expiresHint', { max: info.maxExpiresOn })" data-testid="ai-expires" />
            <q-input
              v-model="password" type="password" autocomplete="current-password" :label="t('aiTokens.dialog.password')"
              :hint="t('aiTokens.dialog.passwordHint')" dense outlined data-testid="ai-password"
            />
            <div v-if="error" class="text-negative" data-testid="ai-create-error">{{ error }}</div>
          </q-card-section>
          <q-card-actions align="right">
            <q-btn flat no-caps :label="t('dynamic.cancel')" data-testid="ai-cancel" @click="close" />
            <q-btn type="submit" color="primary" no-caps :label="t('aiTokens.dialog.submit')" :loading="create.isPending.value" data-testid="ai-submit" />
          </q-card-actions>
        </q-form>
      </template>
      <template v-else>
        <q-card-section class="text-h6">{{ t('aiTokens.secret.title') }}</q-card-section>
        <q-card-section class="q-pt-none scroll ai-secret-scroll">
          <q-banner dense class="bg-negative-soft q-mb-md" data-testid="ai-secret-warn">{{ t('aiTokens.secret.warn') }}</q-banner>
          <div class="row items-start no-wrap q-mb-md">
            <code class="ai-code col" data-testid="ai-secret">{{ secret }}</code>
            <q-btn flat dense no-caps icon="content_copy" :label="copyState === 'copied' ? t('aiTokens.secret.copied') : t('aiTokens.secret.copy')" class="q-ml-sm" data-testid="ai-copy" @click="copy" />
          </div>
          <div v-if="copyState === 'failed'" class="text-negative q-mb-sm" data-testid="ai-copy-failed">{{ t('aiTokens.secret.copyFailed') }}</div>
          <div class="text-subtitle2">{{ t('aiTokens.secret.env') }}</div>
          <pre class="ai-code q-mb-md" data-testid="ai-example-env">{{ envExample }}</pre>
          <div class="text-subtitle2">{{ t('aiTokens.secret.claude') }}</div>
          <pre class="ai-code q-mb-md" data-testid="ai-example-claude">{{ info.examples.claudeCode }}</pre>
          <div class="text-subtitle2">{{ t('aiTokens.secret.codex') }}</div>
          <pre class="ai-code" data-testid="ai-example-codex">{{ info.examples.codex }}</pre>
        </q-card-section>
        <q-card-actions align="right">
          <q-btn color="primary" no-caps :label="t('aiTokens.secret.done')" data-testid="ai-secret-close" @click="confirmClose = true" />
        </q-card-actions>
      </template>
    </q-card>
    <q-dialog v-model="confirmClose">
      <q-card style="width: 400px; max-width: 95vw" data-testid="ai-close-confirm">
        <q-card-section class="text-h6">{{ t('aiTokens.secret.closeTitle') }}</q-card-section>
        <q-card-section class="q-pt-none">{{ t('aiTokens.secret.closeBody') }}</q-card-section>
        <q-card-actions align="right">
          <q-btn v-close-popup color="primary" no-caps :label="t('aiTokens.secret.closeKeep')" data-testid="ai-close-keep" />
          <q-btn flat no-caps :label="t('aiTokens.secret.closeAnyway')" data-testid="ai-close-anyway" @click="close" />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </q-dialog>
</template>

<style scoped>
.ai-fieldset { border: 1px solid var(--t-border); border-radius: var(--t-radius); margin: 0; padding: 4px 12px 8px; }
.ai-secret-scroll { max-height: 70vh; }
.ai-code {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 12px;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  word-break: break-all;
  margin: 0;
  padding: 8px;
  border: 1px solid var(--t-border);
  border-radius: var(--t-radius);
}
</style>
