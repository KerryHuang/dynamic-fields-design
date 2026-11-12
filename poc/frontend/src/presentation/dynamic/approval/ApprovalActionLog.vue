<script setup lang="ts">
import { date } from 'quasar';
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import type { ApprovalActionView } from 'src/domain/dynamic/approval-types';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { ModuleSchema } from 'src/domain/dynamic/types';
import { fieldTypeRegistry } from 'src/presentation/dynamic/field-type-registry';
import { labelOf } from './approval-display';

const props = defineProps<{ actions: ApprovalActionView[]; schema: ModuleSchema | null }>();
const { t, te, locale } = useI18n();
const timeText = (iso: string) => date.formatDate(new Date(iso), 'YYYY-MM-DD HH:mm');

const fields = computed(() => new Map((props.schema?.blocks ?? []).flatMap((b) => b.fields).map((f) => [f.id, f])));

/** The server stores a Traditional Chinese note for audit; the screen words a reroute or a fallback hand-over in the reader's language. */
function rerouteText(a: ApprovalActionView): string {
  const kind = (a.data as { kind?: string } | null)?.kind;
  return t(`approval.panel.reroute.${kind && ['flow', 'fallback', 'pick'].includes(kind) ? kind : 'generic'}`);
}

interface Change { fieldId: number; label: string; before: string; after: string }
/** Fields the schema does not know are left out (the server already hides what the reader may not see). */
function changes(a: ApprovalActionView): Change[] {
  if (a.action !== 'content-changed' || !Array.isArray(a.data)) return [];
  return (a.data as { fieldId: number; before: unknown; after: unknown }[]).flatMap((c) => {
    const f = fields.value.get(c.fieldId);
    if (!f) return [];
    const fmt = (v: unknown) => fieldTypeRegistry[f.type].format(v, f, locale.value);
    return [{ fieldId: c.fieldId, label: resolveI18n(f.label, locale.value), before: fmt(c.before), after: fmt(c.after) }];
  });
}
</script>

<template>
  <ul class="q-pl-none q-my-none approval-log" data-testid="approval-log">
    <li v-for="(a, i) in actions" :key="i" class="q-mb-sm approval-wrap" :data-testid="`log-${a.action}`">
      <div>
        <span class="text-weight-medium">{{ labelOf('approval.action', a.action, t, te) }}</span>
        <span class="text-muted"> · {{ a.actorName }} · {{ timeText(a.createdTime) }}</span>
      </div>
      <div v-if="a.action === 'reroute'" class="q-pl-sm" data-testid="log-reroute">{{ rerouteText(a) }}</div>
      <div v-else-if="a.action === 'fallback'" class="q-pl-sm" data-testid="log-fallback">{{ t('approval.panel.fallbackNote') }}</div>
      <div v-else-if="a.comment" class="q-pl-sm" data-testid="log-comment">{{ a.comment }}</div>
      <ul v-if="changes(a).length" class="q-my-none">
        <li v-for="c in changes(a)" :key="c.fieldId">{{ t('approval.panel.changed', { field: c.label, before: c.before || '—', after: c.after || '—' }) }}</li>
      </ul>
    </li>
  </ul>
</template>

<style scoped>
.approval-log { list-style: none; }
.approval-wrap { overflow-wrap: anywhere; }
</style>
