<script setup lang="ts">
import RowActionMenu from 'src/components/framework/RowActionMenu.vue';
import { useQuasar } from 'quasar';
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useMe } from 'src/application/dynamic/queries/permissions-queries';
import { useRulesQuery, useSaveRulesMutation } from 'src/application/dynamic/queries/rules-queries';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { ConditionNode, ModuleDetail, Rule } from 'src/domain/dynamic/types';
import { leftoverMessages } from 'src/presentation/dynamic/designer/api-error';
import { toSchemaFields } from 'src/presentation/dynamic/designer/schema-fields';
import type { ApiError } from 'src/services/api/http';
import RuleEditorDialog from './RuleEditorDialog.vue';
import { conditionSummary } from './condition-summary';
import { joinList } from 'src/i18n/translate';

const MAX_RULES = 100;

const props = defineProps<{ module: ModuleDetail }>();
const { t, locale } = useI18n();
const $q = useQuasar();

const key = computed(() => props.module.key);
const rulesQuery = useRulesQuery(key);
const me = useMe();
const save = useSaveRulesMutation(key);

const fields = computed(() => toSchemaFields(props.module));
const blocks = computed(() => props.module.blocks.map((b) => ({ id: b.id, label: b.label })));

const draft = ref<Rule[] | null>(null);
const errors = ref<Record<string, string[]>>({});
// data.value is a readonly reactive proxy; the data is plain JSON. Following data also drops the previous module's draft.
watch(() => rulesQuery.data.value, (d) => {
  draft.value = d ? JSON.parse(JSON.stringify(d)) : null;
  errors.value = {};
}, { immediate: true });

const readonly = computed(() => !me.data.value?.designer.edit);
const ready = computed(() => draft.value && me.data.value);

/** Error keys index into the array that was sent; any edit reshuffles rows, so marks must not outlive it. */
function edit(fn: (rules: Rule[]) => void) {
  fn(draft.value!);
  errors.value = {};
}
const rowError = (i: number) => {
  const hit = Object.entries(errors.value).filter(([k]) => k.startsWith(`rules[${i}]`)).flatMap(([, v]) => v);
  return hit.length ? joinList(hit) : undefined;
};

function move(i: number, delta: -1 | 1) {
  edit((rules) => { [rules[i], rules[i + delta]] = [rules[i + delta]!, rules[i]!]; });
}
const remove = (i: number) => edit((rules) => { rules.splice(i, 1); });
const toggleActive = (i: number) => edit((rules) => { rules[i]!.active = !rules[i]!.active; });

// ---- dialog ----
const dialog = ref(false);
const editingIndex = ref<number | null>(null);
const editingRule = computed(() => (editingIndex.value === null ? undefined : draft.value?.[editingIndex.value]));
function open(i: number | null) {
  editingIndex.value = i;
  dialog.value = true;
}
function onSave(rule: Rule) {
  edit((rules) => {
    if (editingIndex.value === null) rules.push(rule);
    else rules[editingIndex.value] = rule;
  });
}

// ---- row text ----
const fieldById = (id: number) => fields.value.find((f) => f.id === id);
const fieldLabel = (id: number) => { const f = fieldById(id); return f ? resolveI18n(f.label, locale.value) : `#${id}`; };

const summary = (n: ConditionNode) => conditionSummary(n, fields.value, t, locale.value);
function targetText(target: string): string {
  if (target === '*') return t('designer.rules.targetRecord');
  const [type, id] = [target.split(':')[0], Number(target.split(':')[1])];
  if (type === 'block') {
    const b = props.module.blocks.find((x) => x.id === id);
    return b ? resolveI18n(b.label, locale.value) : `#${id}`;
  }
  return fieldLabel(id);
}

function submit() {
  if (!draft.value) return;
  errors.value = {};
  save.mutate(draft.value, {
    onSuccess: () => $q.notify({ type: 'positive', message: t('designer.rules.saved') }),
    onError: (e) => {
      const err = e as unknown as ApiError;
      errors.value = err.errors ?? {};
      const rest = leftoverMessages(err, ['rules[']);
      $q.notify({ type: 'negative', message: rest.length ? joinList(rest) : t('designer.saveFailed') });
    },
  });
}
</script>

<template>
  <div v-if="rulesQuery.isError.value" class="text-negative">{{ t('dynamic.loadFailed') }}</div>
  <div v-else-if="!ready" class="text-muted">{{ t('designer.loading') }}</div>
  <div v-else>
    <div class="row items-center q-mb-sm">
      <q-space />
      <q-btn
        color="primary" icon="add" dense :label="t('designer.rules.add')" :disable="readonly || draft!.length >= MAX_RULES"
        data-testid="add-rule" @click="open(null)"
      >
        <q-tooltip v-if="draft!.length >= MAX_RULES">{{ t('designer.rules.limit') }}</q-tooltip>
      </q-btn>
    </div>

    <q-list bordered separator>
      <q-item v-if="!draft!.length"><q-item-section class="text-muted">{{ t('designer.rules.empty') }}</q-item-section></q-item>
      <q-item
        v-for="(r, i) in draft" :key="i" :clickable="!readonly" :class="{ 'text-muted': !r.active, 'rule-row--error': !!rowError(i) }"
        :data-testid="`rule-row-${i}`" @click="!readonly && open(i)"
      >
        <q-item-section>
          <q-item-label>
            <q-badge :label="t(`designer.rules.kinds.${r.kind}`)" />
            <q-badge v-if="!r.active" color="muted" class="q-ml-sm">{{ t('designer.rules.inactive') }}</q-badge>
          </q-item-label>
          <q-item-label caption>{{ t('designer.rules.condition') }}：{{ summary(r.condition) }}</q-item-label>
          <q-item-label caption>{{ t('designer.rules.targets') }}：{{ joinList(r.targets.map(targetText)) }}</q-item-label>
          <q-item-label v-if="rowError(i)" class="text-negative" :data-testid="`rule-error-${i}`">{{ rowError(i) }}</q-item-label>
        </q-item-section>
        <q-item-section side>
          <RowActionMenu
            v-if="$q.screen.lt.sm" :testid="`rule-more-${i}`"
            :actions="[
              { label: t('designer.moveUp'), disable: readonly || i === 0, run: () => move(i, -1) },
              { label: t('designer.moveDown'), disable: readonly || i === draft!.length - 1, run: () => move(i, 1) },
              { label: r.active ? t('designer.rules.deactivate') : t('designer.rules.activate'), disable: readonly, run: () => toggleActive(i) },
              { label: t('designer.rules.delete'), disable: readonly, negative: true, run: () => remove(i) },
            ]"
          />
          <div v-else class="row no-wrap items-center" @click.stop>
            <q-btn flat dense round icon="arrow_upward" :title="t('designer.moveUp')" :disable="readonly || i === 0" :data-testid="`rule-up-${i}`" @click="move(i, -1)" />
            <q-btn
              flat dense round icon="arrow_downward" :title="t('designer.moveDown')" :disable="readonly || i === draft!.length - 1"
              :data-testid="`rule-down-${i}`" @click="move(i, 1)"
            />
            <q-btn
              flat dense round :icon="r.active ? 'toggle_on' : 'toggle_off'" :title="r.active ? t('designer.rules.deactivate') : t('designer.rules.activate')"
              :disable="readonly" :data-testid="`rule-active-${i}`" @click="toggleActive(i)"
            />
            <q-btn flat dense round icon="delete" color="negative" :title="t('designer.rules.delete')" :disable="readonly" :data-testid="`rule-delete-${i}`" @click="remove(i)" />
          </div>
        </q-item-section>
      </q-item>
    </q-list>

    <div class="row justify-end q-mt-md">
      <q-btn color="primary" :label="t('designer.rules.save')" :disable="readonly" :loading="save.isPending.value" data-testid="save-rules" @click="submit" />
    </div>

    <RuleEditorDialog v-model="dialog" :fields="fields" :blocks="blocks" :rule="editingRule" @save="onSave" />
  </div>
</template>

<style scoped>
.rule-row--error { border-left: 3px solid var(--q-negative); }
</style>
