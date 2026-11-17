<script setup lang="ts">
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { moveTargets, type BoardCard, type BoardColumn, type BoardGroupBy } from 'src/domain/dynamic/task-board-types';
import MoveTargetList from './MoveTargetList.vue';

/**
 * 卡片右上角的「移到…」按鈕（D2）：桌機開 q-menu，手機（一次只看一欄）開底部全寬對話框。
 * 按鈕與選單都在卡片的開啟按鈕之外，不巢狀在其他互動元件裡。
 */
const props = defineProps<{ card: BoardCard; columns: readonly BoardColumn[]; groupBy: BoardGroupBy; labelOf: (key: string) => string; phone: boolean }>();
const emit = defineEmits<{ move: [columnKey: string] }>();
const { t } = useI18n();

const open = ref(false);
// 選單內容開啟後才會讀到 targets，一屏上百張卡不會每張都先算一次
const targets = computed(() => moveTargets(props.card, props.columns, props.groupBy));
function pick(key: string) {
  open.value = false;
  emit('move', key);
}
</script>

<template>
  <q-btn
    flat round dense size="sm" icon="drive_file_move" class="board-card-move" :aria-label="t('taskBoard.moveTo.buttonFor', { title: card.title })"
    :title="t('taskBoard.moveTo.button')" aria-haspopup="menu" :aria-expanded="open" :data-testid="`board-card-move-${card.id}`" @click="phone && (open = true)"
  >
    <q-menu v-if="!phone" v-model="open" auto-close anchor="bottom right" self="top right">
      <MoveTargetList :targets="targets" :label-of="labelOf" @pick="pick" />
    </q-menu>
    <q-dialog v-else v-model="open" position="bottom">
      <q-card class="full-width move-sheet" data-testid="move-sheet">
        <q-card-section class="text-subtitle1 ellipsis-2-lines">{{ t('taskBoard.moveTo.title') }}　{{ card.title }}</q-card-section>
        <MoveTargetList :targets="targets" :label-of="labelOf" @pick="pick" />
        <q-card-actions align="right"><q-btn v-close-popup flat no-caps :label="t('tasks.cancel')" /></q-card-actions>
      </q-card>
    </q-dialog>
  </q-btn>
</template>

<style scoped>
.board-card-move { position: absolute; top: 4px; right: 4px; z-index: 1; }
.move-sheet { max-width: 100%; }
</style>
