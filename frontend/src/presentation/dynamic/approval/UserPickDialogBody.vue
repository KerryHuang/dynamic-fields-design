<script setup lang="ts">
import { useI18n } from 'vue-i18n';
import GenericSelect, { type SelectOption } from 'src/components/framework/GenericSelect.vue';

// The content shared by the dialogs that hand a task to someone (transfer now; reassign and add-sign later): a hint, a user select and a comment.
defineProps<{
  hint: string; userLabel: string; commentLabel: string; candidates: SelectOption[]; userError?: string; commentError?: string;
  maxComment?: number; topError?: string; stale?: boolean; testid: string;
}>();
const userId = defineModel<string | null>('userId', { required: true });
const comment = defineModel<string>('comment', { required: true });
defineEmits<{ reload: [] }>();
const { t } = useI18n();
</script>

<template>
  <div class="q-mb-md approval-wrap">{{ hint }}</div>
  <div class="q-mb-md" :data-testid="`${testid}-user`">
    <GenericSelect v-model="userId" :label="userLabel" :options="candidates" :error="userError" required />
  </div>
  <q-input
    v-model="comment" type="textarea" outlined autogrow :label="commentLabel" :maxlength="maxComment" :counter="maxComment !== undefined"
    :error="!!commentError" :error-message="commentError" :data-testid="`${testid}-comment`"
  />
  <div v-if="topError" class="text-negative q-mt-md approval-wrap" data-testid="action-error">
    {{ topError }}
    <q-btn v-if="stale" flat dense color="primary" :label="t('dynamic.reload')" data-testid="action-reload" @click="$emit('reload')" />
  </div>
</template>

<style scoped>
.approval-wrap { overflow-wrap: anywhere; }
</style>
