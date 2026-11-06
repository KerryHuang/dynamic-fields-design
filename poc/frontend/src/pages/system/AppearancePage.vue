<script setup lang="ts">
import { Notify } from 'quasar';
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useMe } from 'src/application/dynamic/queries/permissions-queries';
import { useSaveSiteTheme } from 'src/application/dynamic/queries/theme-queries';
import ForbiddenNotice from 'src/components/framework/ForbiddenNotice.vue';
import type { ThemeDefaults } from 'src/domain/theme/theme-model';
import ThemeChooser from 'src/presentation/theme/ThemeChooser.vue';

const { t } = useI18n();
const me = useMe();
const save = useSaveSiteTheme();

/** 全站設定影響所有人，所以用草稿、按「儲存」才送出，不像個人設定選了就存。 */
const draft = ref<ThemeDefaults | null>(null);
const error = ref('');
const saved = computed<ThemeDefaults | null>(() => {
  const th = me.data.value?.theme;
  return th ? { mode: th.defaultMode, style: th.defaultStyle } : null;
});
watch(saved, (s) => { if (s && !draft.value) draft.value = { ...s }; }, { immediate: true });

const readOnly = computed(() => me.data.value?.theme.canSave === false);

function submit() {
  if (!draft.value) return;
  const before = saved.value;
  error.value = '';
  save.mutate(draft.value, {
    onSuccess: () => Notify.create({ type: 'positive', message: t('theme.adminSaved') }),
    onError: () => {
      error.value = t('theme.saveFailed');
      if (before) draft.value = { ...before };
    },
  });
}
</script>

<template>
  <q-page padding>
    <ForbiddenNotice v-if="me.data.value && !me.data.value.isSystemAdmin" />
    <div v-else-if="me.isError.value" class="text-negative" data-testid="appearance-load-error">{{ t('theme.adminLoadFailed') }}</div>
    <template v-else-if="draft">
      <div class="text-h6 q-mb-xs">{{ t('theme.adminTitle') }}</div>
      <div class="text-muted q-mb-md">{{ t('theme.adminHint') }}</div>
      <q-banner v-if="readOnly" dense class="bg-info-soft q-mb-md" data-testid="appearance-demo-readonly">
        {{ t('theme.adminDemoReadOnly') }}
      </q-banner>
      <ThemeChooser
        :mode="draft.mode" :selected-style="draft.style" :defaults="draft" :allow-follow="false" :disabled="readOnly"
        @update:mode="(v) => v && (draft = { ...draft!, mode: v })" @update:style="(v) => v && (draft = { ...draft!, style: v })"
      />
      <div v-if="error" class="text-negative q-mt-md" data-testid="appearance-error">{{ error }}</div>
      <q-btn
        v-if="!readOnly" class="q-mt-md" color="primary" unelevated no-caps :label="t('theme.adminSave')"
        :loading="save.isPending.value" data-testid="appearance-save" @click="submit"
      />
    </template>
  </q-page>
</template>
