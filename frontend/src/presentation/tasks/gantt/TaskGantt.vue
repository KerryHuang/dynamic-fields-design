<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useFetchGanttToday, useGantt } from 'src/application/dynamic/queries/task-board-queries';
import type { BoardCard, GanttSource, TaskGanttConfig, TaskGanttData, TaskGanttWindow } from 'src/domain/dynamic/task-board-types';
import {
  barGeometry, canSchedule, dayAtX, dayNumber, draggedDates, formatGanttDate, ganttLines, ganttWeeks, GANTT_LEFT_WIDTH, GANTT_LEFT_WIDTH_TABLET, minorTicks,
  monthTicks, phoneWindow, shiftPhoneWindow, shiftWindow, spanGeometry, todayPosition, windowDays, windowFor, type GanttDragPart, type GanttLine, type GanttScale,
} from 'src/domain/dynamic/task-gantt';
import { progressView, taskStatusColor } from 'src/domain/dynamic/task-types';
import SubtaskProgress from 'src/presentation/dynamic/tasks/SubtaskProgress.vue';
import { useCardDrag } from '../board/use-card-drag';
import GanttDateDialog from './GanttDateDialog.vue';
import { useGanttSchedule } from './use-gantt-schedule';

/**
 * 甘特圖（看板設計 §11.5.4）：預設甘特圖、刻度與平移、三種分組、子任務跨度、今天線、未排程（gt2a）；
 * 平板時間軸、手機依週分組清單、日期對話框與「編輯日期」「排程」鈕（gt2b1）；桌機筆電拖拉改日期（gt2b2）。
 * 時間軸用 CSS grid 與百分比定位自畫（GT-T21），寬度填滿容器、不橫向捲動；用 Quasar 視窗斷點判斷寬度（GT-T17）。
 */
const emit = defineEmits<{ open: [id: number] }>();
const { t, locale } = useI18n();
const $q = useQuasar();

// 觸控（主要指標是手指）一律當平板處理，與寬度無關（§11.5.4.2）
const coarseQuery = typeof window !== 'undefined' && typeof window.matchMedia === 'function' ? window.matchMedia('(pointer: coarse)') : null;
const coarse = ref(!!coarseQuery?.matches);
const onPointerChange = () => { coarse.value = !!coarseQuery?.matches; };
coarseQuery?.addEventListener?.('change', onPointerChange);
onBeforeUnmount(() => coarseQuery?.removeEventListener?.('change', onPointerChange));
// 手機 < 600 是週清單；600–1023 或觸控是平板（左欄 200px、點長條開日期對話框）；其餘是桌機筆電
const mode = computed<'phone' | 'tablet' | 'desktop'>(() => ($q.screen.lt.sm ? 'phone' : $q.screen.lt.md || coarse.value ? 'tablet' : 'desktop'));

// 刻度與時間窗由使用者在畫面上切換；沒切過就照伺服器（預設甘特圖的刻度、伺服器算的預設時間窗）。
// 換了刻度後「今天」要用新刻度的預設時間窗，所以把刻度放進設定一起送（只改刻度，其餘是伺服器回的設定）。
const scale = ref<GanttScale | null>(null);
const win = ref<TaskGanttWindow | null>(null);
const base = ref<TaskGanttConfig | null>(null);
// 手機固定 4 週：第一次先照預設查（要伺服器的今天與週起始日），拿到後改查從本週起的 4 週
const phoneWin = ref<TaskGanttWindow | null>(null);
const source = computed<GanttSource>(() => (mode.value === 'phone'
  ? { viewId: null, config: null, window: phoneWin.value }
  : {
    viewId: null,
    config: scale.value && base.value && scale.value !== base.value.scale ? { ...base.value, scale: scale.value } : null,
    window: win.value,
  }));
const query = useGantt(source);
const data = computed(() => query.data.value ?? null);
watch(data, (d) => { if (d && !base.value) base.value = d.config; }, { immediate: true });
const shownScale = computed<GanttScale>(() => scale.value ?? data.value?.config.scale ?? 'week');
// 上一段還沒換成新的一段前不能再平移（平移以畫面上的時間窗為起點）
const busy = computed(() => query.isPlaceholderData.value);

// ---- 手機週清單（§11.5.4.7） ----
const phoneData = ref<TaskGanttData | null>(null);
watch([data, mode], ([d, m]) => {
  if (m !== 'phone' || !d) return;
  if (!phoneWin.value) phoneWin.value = phoneWindow(d.today, d.weekStart);
  else if (d.window.from === phoneWin.value.from && d.window.to === phoneWin.value.to) phoneData.value = d;
}, { immediate: true });
// 忙碌看實際請求：新的一段還在查（畫面保留上一段）或「今天」正在問伺服器；請求失敗就不算忙，導覽鈕與重試都可按
const todayPending = ref(false);
const phoneBusy = computed(() => !!phoneData.value && (query.isPlaceholderData.value || todayPending.value));
const weeks = computed(() => (phoneData.value ? ganttWeeks(phoneData.value.rows, phoneData.value.window) : []));
function shiftPhone(dir: 1 | -1) {
  if (phoneData.value) phoneWin.value = shiftPhoneWindow(phoneData.value.window, dir);
}
// 跨過午夜（甚至跨週）後畫面上的 today 已舊：先照預設重查一次拿最新的今天與週起始日，再算本週起的 4 週
const fetchToday = useFetchGanttToday();
async function phoneToday() {
  const d = phoneData.value;
  if (!d || todayPending.value) return;
  todayPending.value = true;
  try {
    const fresh = await fetchToday();
    const w = phoneWindow(fresh.today, fresh.weekStart);
    if (w.from === d.window.from && w.to === d.window.to) await query.refetch();
    else phoneWin.value = w;
  } catch {
    // 失敗時留在原本那 4 週；錯誤不另提示，再按一次即可
  } finally {
    todayPending.value = false;
  }
}
const phoneRange = computed(() => (phoneData.value ? `${fullDate(phoneData.value.window.from)} – ${fullDate(phoneData.value.window.to)}` : ''));
const phoneDates = (c: BoardCard) => (c.startDate && c.dueDate ? `${shortDate(c.startDate)} → ${shortDate(c.dueDate)}` : c.dueDate ? `◆ ${shortDate(c.dueDate)}` : '');

// ---- 改日期（§11.5.4.9）：平板點長條、任何寬度的「編輯日期」、未排程的「排程」 ----
const scheduler = useGanttSchedule(source);
const datesOpen = ref(false);
const datesCard = ref<BoardCard | null>(null);
function openDates(c: BoardCard) {
  datesCard.value = c;
  datesOpen.value = true;
}
// 平板不能拖，點長條改開日期對話框；不能改日期的任務照常開任務詳細（§11.5.4.8 不能改的列）
function onBar(c: BoardCard) {
  if (mode.value === 'tablet' && canSchedule(c)) openDates(c);
  else emit('open', c.id);
}

// ---- 拖拉（§11.5.4.8）：只有桌機筆電、用滑鼠或觸控筆；觸控一律不拖（GT-T17）。沿用看板的拖拉（門檻、Esc、pointercancel、拖完吞掉 click），
// 「欄鍵」換成指標底下那一天（yyyy-MM-dd），放開時照拖的部分算新日期，走與日期對話框同一個 schedule 流程。
const axisEl = ref<HTMLElement | null>(null);
const bodyEl = ref<HTMLElement | null>(null);
interface DragSource { card: BoardCard; part: GanttDragPart; originDay: string | null }
const dragSource = ref<DragSource | null>(null);
const canDrag = computed(() => mode.value === 'desktop' && !busy.value);
const draggable = (c: BoardCard) => mode.value === 'desktop' && canSchedule(c);
function pointerDay(x: number): string | null {
  return axisEl.value && data.value ? dayAtX(x, axisEl.value.getBoundingClientRect(), data.value.window) : null;
}
// 長條拖出時間軸左右時夾在時間窗的第一天、最後一天；上下拖不換組（只看 x）。未排程的列要放在時間軸上才算數，放在別處不排程。
function hitTest(x: number, y: number): string | null {
  if (dragSource.value?.part === 'unscheduled') {
    const body = bodyEl.value?.getBoundingClientRect();
    const axis = axisEl.value?.getBoundingClientRect();
    if (!body || !axis || y < body.top || y > body.bottom || x < axis.left || x > axis.right) return null;
  }
  return pointerDay(x);
}
const drag = useCardDrag({ enabled: canDrag, hitTest, onDrop: (_id, day) => { void drop(day); } });
function nextDates(src: DragSource, day: string) {
  const delta = src.originDay ? dayNumber(day) - dayNumber(src.originDay) : 0;
  return draggedDates(src.part, src.card.startDate, src.card.dueDate, delta, day);
}
function onDragStart(e: PointerEvent, card: BoardCard, part: GanttDragPart) {
  // 同一個任務的請求還沒回來前不能再拖（同看板）；整條與兩緣看按下的位置
  if (!canDrag.value || !canSchedule(card) || e.pointerType === 'touch' || scheduler.isPending(card.id)) return;
  const handle = (e.target as Element | null)?.closest?.('[data-drag-part]')?.getAttribute('data-drag-part') as GanttDragPart | null | undefined;
  const originDay = pointerDay(e.clientX);
  if (part !== 'unscheduled' && !originDay) return;
  dragSource.value = { card, part: part === 'bar' && handle ? handle : part, originDay };
  drag.start(e, card.id);
}
const dragPreview = computed(() => {
  const st = drag.state.value;
  const src = dragSource.value;
  if (!st || !src || st.cardId !== src.card.id || st.overKey === null) return null;
  return { id: src.card.id, ...nextDates(src, st.overKey) };
});
async function drop(day: string) {
  const src = dragSource.value;
  dragSource.value = null;
  if (!src) return;
  const next = nextDates(src, day);
  if (next.startDate === src.card.startDate && next.dueDate === src.card.dueDate) return;
  const outcome = await scheduler.schedule(src.card, next.startDate, next.dueDate);
  // 拖拉沒有欄位可掛：400 的訊息改用通知（其他失敗 useGanttSchedule 已經通知過），長條由 useScheduleTask 放回原位
  const field = outcome && !outcome.ok && outcome.fields ? outcome.fields.startDate ?? outcome.fields.dueDate : null;
  if (field) $q.notify({ type: 'negative', message: field, position: 'bottom' });
}
const dayLabel = (iso: string) => t('taskGantt.drag.day', {
  date: formatGanttDate(iso, locale.value, { month: 'numeric', day: 'numeric' }), weekday: formatGanttDate(iso, locale.value, { weekday: 'short' }),
});
const dragTip = computed(() => {
  const st = drag.state.value;
  const src = dragSource.value;
  if (!st || !src) return null;
  const p = dragPreview.value;
  let text = '';
  if (p?.startDate && p.dueDate) {
    const n = windowDays({ from: p.startDate, to: p.dueDate });
    text = n === 1 ? t('taskGantt.drag.oneDay', { date: dayLabel(p.dueDate) }) : t('taskGantt.drag.range', { from: dayLabel(p.startDate), to: dayLabel(p.dueDate), n });
  } else if (p?.dueDate) text = t('taskGantt.dueOnly', { date: dayLabel(p.dueDate) });
  return { x: st.x, y: st.y, title: src.part === 'unscheduled' ? src.card.title : '', text };
});
const dragClass = (c: BoardCard) => (mode.value !== 'desktop' ? {} : {
  'is-draggable': canSchedule(c), 'is-locked': !canSchedule(c), 'is-dragging': dragPreview.value?.id === c.id,
});
const leftWidth = computed(() => (mode.value === 'tablet' ? GANTT_LEFT_WIDTH_TABLET : GANTT_LEFT_WIDTH));

function shift(dir: 1 | -1) {
  if (data.value) win.value = shiftWindow(data.value.window, shownScale.value, dir);
}
function setScale(s: GanttScale) {
  scale.value = s;
  if (data.value) win.value = windowFor(s, data.value.window.from, data.value.weekStart);
}
// 已在預設時間窗時清成 null 不會改變查詢鍵；跨過午夜後「今天」要另外重抓，伺服器才會回新的今天與時間窗。
const goToday = () => {
  if (win.value === null) void query.refetch();
  else win.value = null;
};
const scaleOptions = computed(() => (['day', 'week', 'month'] as const).map((v) => ({ value: v, label: t(`taskGantt.scale.${v}`), attrs: { 'data-testid': `gantt-scale-${v}` } })));

const fullDate = (iso: string) => formatGanttDate(iso, locale.value, { year: 'numeric', month: '2-digit', day: '2-digit' });
const rangeText = computed(() => (data.value ? `${fullDate(data.value.window.from)} – ${fullDate(data.value.window.to)}` : ''));
const windowOf = computed(() => data.value?.window ?? { from: '', to: '' });
const majors = computed(() => (data.value ? monthTicks(data.value.window) : []).map((x) => ({
  ...x, label: formatGanttDate(x.date, locale.value, { year: 'numeric', month: 'short' }),
})));
const minors = computed(() => (data.value ? minorTicks(data.value.window, shownScale.value) : []).map((x) => ({
  ...x,
  label: shownScale.value === 'day'
    ? `${formatGanttDate(x.date, locale.value, { day: 'numeric' })} ${formatGanttDate(x.date, locale.value, { weekday: 'short' })}`
    : formatGanttDate(x.date, locale.value, { month: 'numeric', day: 'numeric' }),
})));
// 格線：日、週刻度畫在每一格的左緣，月刻度畫在每月一日
const gridlines = computed(() => (shownScale.value === 'month' ? majors.value : minors.value).slice(1).map((x) => x.left));
const today = computed(() => (data.value ? todayPosition(data.value.today, data.value.window) : null));

const collapsed = ref(new Set<number>());
function toggle(id: number) {
  const next = new Set(collapsed.value);
  if (!next.delete(id)) next.add(id);
  collapsed.value = next;
}
const collator = computed(() => new Intl.Collator(locale.value));
const lines = computed<GanttLine[]>(() => (data.value ? ganttLines(data.value, data.value.config.groupBy, collapsed.value, collator.value.compare) : []));

const pct = (n: number) => `${(n * 100).toFixed(4)}%`;
const assigneeLabel = (c: BoardCard) => {
  if (!c.assigneeUserId) return t('tasks.unassigned');
  const name = c.assigneeName ?? c.assigneeUserId;
  return c.assigneeActive ? name : t('tasks.inactiveUser', { name });
};
const groupLabel = (l: Extract<GanttLine, { kind: 'group' }>) => {
  if (l.userId === null) return t('tasks.unassigned');
  const name = l.name ?? l.userId;
  return l.inactive ? t('tasks.inactiveUser', { name }) : name;
};
const closed = (c: BoardCard) => c.status === 'done' || c.status === 'cancelled';
const dates = (c: BoardCard) => (c.startDate && c.dueDate
  ? `${fullDate(c.startDate)} – ${fullDate(c.dueDate)}`
  : c.dueDate ? t('taskGantt.dueOnly', { date: fullDate(c.dueDate) }) : '');
const barLabel = (c: BoardCard) => `${c.title}${t('tasks.colon')}${dates(c)}`;
const progressText = (c: BoardCard) => {
  const v = progressView(c.progress);
  return v ? `${t('tasks.subtask.progress')} ${v.done}／${v.denominator}` : '';
};
const barClass = (c: BoardCard) => ({ 'is-overdue': c.overdue && !closed(c), 'is-closed': closed(c) });
const spanLabel = (l: { span: { from: string; to: string } | null }) => (l.span ? t('taskGantt.span', { from: fullDate(l.span.from), to: fullDate(l.span.to) }) : '');
// 拖拉中的那一條畫在新位置；Esc 或放開後回到快取裡的日期（樂觀更新、失敗放回由 useScheduleTask 處理）
const geometry = (c: BoardCard) => {
  const p = dragPreview.value;
  const d = p && p.id === c.id ? p : c;
  return barGeometry(d.startDate, d.dueDate, windowOf.value);
};
const spanOf = (l: { span: { parentId: number; from: string; to: string } | null }) => spanGeometry(l.span, windowOf.value);
const unscheduledMore = computed(() => (data.value ? data.value.unscheduledTotal - data.value.unscheduled.length : 0));
const shortDate = (iso: string) => formatGanttDate(iso, locale.value, { month: 'numeric', day: 'numeric' });
</script>

<template>
  <div class="task-gantt" :class="dragSource && drag.state.value ? `is-dragging is-dragging-${dragSource.part}` : ''" role="region" :aria-label="t('taskGantt.region')" data-testid="gantt">
    <!-- 手機：不畫時間軸，依週分組的清單；點一列開任務詳細（§11.5.4.7） -->
    <template v-if="mode === 'phone'">
      <div class="row items-center no-wrap q-mb-sm" style="gap: 4px">
        <q-btn flat dense round icon="chevron_left" :disable="!phoneData || phoneBusy" :aria-label="t('taskGantt.prev')" data-testid="gantt-prev" @click="shiftPhone(-1)" />
        <q-btn flat dense no-caps :disable="!phoneData || phoneBusy" :label="t('taskGantt.today')" data-testid="gantt-today" @click="phoneToday" />
        <q-btn flat dense round icon="chevron_right" :disable="!phoneData || phoneBusy" :aria-label="t('taskGantt.next')" data-testid="gantt-next" @click="shiftPhone(1)" />
        <span class="text-body2 ellipsis" aria-live="polite" data-testid="gantt-range">{{ phoneRange }}</span>
      </div>
      <div v-if="query.error.value" class="text-negative q-py-sm" data-testid="gantt-load-failed">
        {{ t('taskGantt.loadFailed') }}
        <q-btn flat dense no-caps color="primary" :label="t('taskBoard.retry')" data-testid="gantt-retry" @click="query.refetch()" />
      </div>
      <q-linear-progress v-else-if="!phoneData" indeterminate class="q-my-sm" data-testid="gantt-loading" />
      <template v-if="phoneData">
        <q-banner v-if="!phoneData.scopeAvailable" dense rounded class="bg-info-soft q-mb-sm" role="status" data-testid="gantt-scope-unavailable">
          {{ t('taskGantt.scopeUnavailable') }}
        </q-banner>
        <q-banner v-if="phoneData.truncated" dense rounded class="bg-info-soft q-mb-sm" role="status" data-testid="gantt-truncated">
          {{ t('taskGantt.truncated', { shown: phoneData.rows.length, total: phoneData.total }) }}
        </q-banner>
        <div class="gantt-weeks" :class="{ 'is-busy': phoneBusy }" :aria-busy="phoneBusy" data-testid="gantt-weeks">
          <section v-for="wk in weeks" :key="wk.from" class="q-mb-md" :aria-label="`${shortDate(wk.from)} – ${shortDate(wk.to)}`" :data-testid="`gantt-week-${wk.from}`">
            <div class="text-subtitle2 q-mb-xs" data-testid="gantt-week-title">{{ shortDate(wk.from) }} – {{ shortDate(wk.to) }}</div>
            <div v-if="!wk.cards.length" class="text-caption text-muted q-px-sm" data-testid="gantt-week-empty">{{ t('taskGantt.weekEmpty') }}</div>
            <q-list v-else dense bordered separator class="rounded-borders">
              <q-item v-for="c in wk.cards" :key="c.id" clickable :data-testid="`gantt-week-item-${c.id}`" @click="emit('open', c.id)">
                <q-item-section>
                  <q-item-label v-if="c.level === 'child' && c.parentTitle" caption class="ellipsis" :data-testid="`gantt-week-parent-${c.id}`">
                    {{ t('tasks.subtask.parent') }}{{ t('tasks.colon') }}{{ c.parentTitle }}
                  </q-item-label>
                  <q-item-label class="gantt-two-lines" :class="{ 'text-strike text-muted': closed(c) }">{{ c.title }}</q-item-label>
                  <q-item-label caption class="row items-center q-gutter-x-sm">
                    <span :class="{ 'text-negative': c.overdue && !closed(c) }" :data-testid="`gantt-week-dates-${c.id}`">
                      <span aria-hidden="true">{{ phoneDates(c) }}</span><span class="sr-only">{{ dates(c) }}</span>
                    </span>
                    <q-badge outline :color="taskStatusColor(c.status)" :label="t(`tasks.status.${c.status}`)" />
                    <SubtaskProgress v-if="c.progress" :progress="c.progress" :bar="false" />
                  </q-item-label>
                </q-item-section>
              </q-item>
            </q-list>
          </section>
        </div>
        <q-expansion-item
          v-if="phoneData.unscheduledTotal > 0" dense dense-toggle switch-toggle-side :label="t('taskGantt.unscheduled', { n: phoneData.unscheduledTotal })"
          header-class="text-subtitle2" data-testid="gantt-unscheduled"
        >
          <q-list dense bordered separator class="rounded-borders">
            <q-item v-for="c in phoneData.unscheduled" :key="c.id" clickable :data-testid="`gantt-unscheduled-${c.id}`" @click="emit('open', c.id)">
              <q-item-section>
                <q-item-label class="gantt-two-lines" :class="{ 'text-strike text-muted': closed(c) }">{{ c.title }}</q-item-label>
                <q-item-label caption class="row items-center q-gutter-x-sm">
                  <span v-if="c.startDate">{{ t('taskGantt.startOnly', { date: shortDate(c.startDate) }) }}</span>
                  <q-badge outline :color="taskStatusColor(c.status)" :label="t(`tasks.status.${c.status}`)" />
                </q-item-label>
              </q-item-section>
            </q-item>
          </q-list>
          <div v-if="phoneData.unscheduledTruncated" class="text-caption text-muted q-mt-xs" role="status" data-testid="gantt-unscheduled-more">
            {{ t('taskGantt.unscheduledMore', { n: phoneData.unscheduledTotal - phoneData.unscheduled.length }) }}
          </div>
        </q-expansion-item>
      </template>
    </template>
    <template v-else>
      <div class="row items-center q-mb-sm" style="gap: 8px">
        <q-btn-toggle
          :model-value="shownScale" :options="scaleOptions" dense no-caps unelevated toggle-color="primary" :aria-label="t('taskGantt.scale.label')" role="group"
          data-testid="gantt-scale" @update:model-value="(v) => setScale(v as GanttScale)"
        />
        <div class="row items-center no-wrap">
          <q-btn flat dense round icon="chevron_left" :disable="!data || busy" :aria-label="t('taskGantt.prev')" data-testid="gantt-prev" @click="shift(-1)" />
          <q-btn flat dense no-caps :disable="!data || busy" :label="t('taskGantt.today')" data-testid="gantt-today" @click="goToday" />
          <q-btn flat dense round icon="chevron_right" :disable="!data || busy" :aria-label="t('taskGantt.next')" data-testid="gantt-next" @click="shift(1)" />
        </div>
        <span class="text-body2" aria-live="polite" data-testid="gantt-range">{{ rangeText }}</span>
      </div>
      <div v-if="query.error.value && !data" class="text-negative q-py-sm" data-testid="gantt-load-failed">
        {{ t('taskGantt.loadFailed') }}
        <q-btn flat dense no-caps color="primary" :label="t('taskBoard.retry')" data-testid="gantt-retry" @click="query.refetch()" />
      </div>
      <q-linear-progress v-else-if="query.isLoading.value" indeterminate class="q-my-sm" data-testid="gantt-loading" />
      <template v-if="data">
        <q-banner v-if="!data.scopeAvailable" dense rounded class="bg-info-soft q-mb-sm" role="status" data-testid="gantt-scope-unavailable">
          {{ t('taskGantt.scopeUnavailable') }}
        </q-banner>
        <q-banner v-if="data.truncated" dense rounded class="bg-info-soft q-mb-sm" role="status" data-testid="gantt-truncated">
          {{ t('taskGantt.truncated', { shown: data.rows.length, total: data.total }) }}
        </q-banner>
        <div class="gantt" :aria-busy="busy" :class="{ 'is-busy': busy, 'is-tablet': mode === 'tablet' }" :style="{ '--gantt-left': `${leftWidth}px` }" data-testid="gantt-chart">
          <div class="gantt-row gantt-head" :class="`is-${shownScale}`">
            <div class="gantt-left text-caption text-muted">{{ t('taskGantt.taskColumn') }}</div>
            <div ref="axisEl" class="gantt-axis" aria-hidden="true" data-testid="gantt-axis">
              <div class="gantt-axis-row">
                <span v-for="m in majors" :key="m.date" class="gantt-tick" :style="{ left: pct(m.left), width: pct(m.width) }" data-testid="gantt-major">{{ m.label }}</span>
              </div>
              <div v-if="minors.length" class="gantt-axis-row">
                <span v-for="m in minors" :key="m.date" class="gantt-tick" :style="{ left: pct(m.left), width: pct(m.width) }" data-testid="gantt-minor">{{ m.label }}</span>
              </div>
              <span v-if="today !== null" class="gantt-today-label text-caption" :style="{ left: pct(today) }" data-testid="gantt-today-label">{{ t('taskGantt.todayLine') }}</span>
            </div>
          </div>
          <div ref="bodyEl" class="gantt-body" data-testid="gantt-body">
            <div class="gantt-overlay" aria-hidden="true">
              <span v-for="(x, i) in gridlines" :key="i" class="gantt-gridline" :style="{ left: pct(x) }" />
              <span v-if="today !== null" class="gantt-today-line" :style="{ left: pct(today) }" data-testid="gantt-today-line" />
            </div>
            <div v-if="!lines.length" class="gantt-empty text-muted q-pa-md" data-testid="gantt-empty">{{ t('taskGantt.empty') }}</div>
            <div
              v-for="line in lines" :key="line.key" class="gantt-row gantt-line" :class="`is-${line.kind}`" :data-testid="`gantt-line-${line.key}`"
            >
              <template v-if="line.kind === 'group'">
                <div class="gantt-left gantt-group text-weight-medium" data-testid="gantt-group">
                  <q-icon name="person" size="16px" class="q-mr-xs" /><span class="ellipsis">{{ groupLabel(line) }}</span>
                  <span class="text-caption text-muted q-ml-xs">{{ t('taskGantt.groupCount', { n: line.count }) }}</span>
                </div>
                <div class="gantt-track" />
              </template>
              <template v-else>
                <div class="gantt-left" :class="{ 'is-child': line.kind === 'task' && line.depth === 1 }">
                  <q-btn
                    v-if="line.childCount > 0" flat dense round size="sm" :icon="line.collapsed ? 'chevron_right' : 'expand_more'" class="gantt-toggle"
                    :aria-expanded="!line.collapsed"
                    :aria-label="t(line.collapsed ? 'taskGantt.expand' : 'taskGantt.collapse', { title: line.kind === 'task' ? line.card.title : line.parent.title })"
                    :data-testid="`gantt-toggle-${line.kind === 'task' ? line.card.id : line.parent.id}`" @click="toggle(line.kind === 'task' ? line.card.id : line.parent.id)"
                  />
                  <span v-else-if="line.kind === 'task' && line.depth === 0" class="gantt-toggle-space" />
                  <template v-if="line.kind === 'parent'">
                    <!-- 父任務不在結果裡：淡色標題（GT-Q2）；看得到才能點開，看不到只露標題（U13） -->
                    <button
                      v-if="line.parent.visible" type="button" class="gantt-title is-faded" :title="t('taskGantt.parentFiltered')"
                      :data-testid="`gantt-parent-open-${line.parent.id}`" @click="emit('open', line.parent.id)"
                    >{{ line.parent.title }}</button>
                    <span v-else class="gantt-title is-faded is-static" :title="t('taskGantt.parentNoAccess')" :data-testid="`gantt-parent-title-${line.parent.id}`">
                      {{ line.parent.title }}<span class="sr-only">{{ t('taskGantt.parentNoAccess') }}</span>
                    </span>
                  </template>
                  <template v-else>
                    <button type="button" class="gantt-title" :class="{ 'text-strike text-muted': closed(line.card) }" :data-testid="`gantt-title-${line.card.id}`" @click="emit('open', line.card.id)">
                      {{ line.card.title }}
                    </button>
                    <div class="gantt-meta">
                      <q-badge outline :color="taskStatusColor(line.card.status)" :label="t(`tasks.status.${line.card.status}`)" :data-testid="`gantt-status-${line.card.id}`" />
                      <SubtaskProgress v-if="line.card.progress" :progress="line.card.progress" :bar="false" class="text-caption" />
                    </div>
                    <!-- 鍵盤與不拖拉時改日期的路徑（§11.5.4.9，比照看板 D2「移到…」）；不能改日期的列不顯示 -->
                    <q-btn
                      v-if="canSchedule(line.card)" flat dense round size="sm" icon="edit_calendar" class="gantt-edit"
                      :aria-label="t('taskGantt.editDates', { title: line.card.title })" :data-testid="`gantt-edit-${line.card.id}`" @click="openDates(line.card)"
                    />
                  </template>
                </div>
                <div class="gantt-track">
                  <span
                    v-if="spanOf(line)" class="gantt-span" :class="{ 'is-faded-row': line.kind === 'parent' }" :style="{ left: pct(spanOf(line)!.left), width: pct(spanOf(line)!.width) }"
                    role="img" :aria-label="spanLabel(line)" :data-testid="`gantt-span-${line.kind === 'task' ? line.card.id : line.parent.id}`"
                  />
                  <template v-if="line.kind === 'task' && geometry(line.card)">
                    <button
                      v-if="geometry(line.card)!.outside" type="button" class="gantt-outside" :class="[`is-${geometry(line.card)!.outside}`, barClass(line.card)]"
                      :aria-label="barLabel(line.card)" :data-testid="`gantt-outside-${line.card.id}`" @click="onBar(line.card)"
                    >
                      <q-icon :name="geometry(line.card)!.outside === 'before' ? 'chevron_left' : 'chevron_right'" size="16px" />
                      <q-tooltip>{{ line.card.title }}<br>{{ dates(line.card) }}</q-tooltip>
                    </button>
                    <button
                      v-else-if="geometry(line.card)!.kind === 'bar'" type="button" class="gantt-bar" :class="[barClass(line.card), dragClass(line.card)]"
                      :style="{ left: pct(geometry(line.card)!.left), width: pct(geometry(line.card)!.width) }"
                      :aria-label="barLabel(line.card)" :data-testid="`gantt-bar-${line.card.id}`" @pointerdown="onDragStart($event, line.card, 'bar')" @click="onBar(line.card)"
                    >
                      <!-- 兩緣的拖拉把手（不能 Tab 到，鍵盤改用「編輯日期」）；那一側被時間窗截掉時看不到實際日期，不給把手，整條仍可拖 -->
                      <template v-if="draggable(line.card)">
                        <span
                          v-if="!geometry(line.card)!.clippedStart" class="gantt-handle is-start" data-drag-part="start" :aria-label="t('taskGantt.drag.startHandle')"
                          :data-testid="`gantt-handle-start-${line.card.id}`"
                        />
                        <span
                          v-if="!geometry(line.card)!.clippedEnd" class="gantt-handle is-end" data-drag-part="due" :aria-label="t('taskGantt.drag.dueHandle')"
                          :data-testid="`gantt-handle-due-${line.card.id}`"
                        />
                      </template>
                      <q-icon v-if="geometry(line.card)!.clippedStart" name="chevron_left" size="14px" class="gantt-clip is-start" data-testid="gantt-clip-start" />
                      <q-icon v-if="geometry(line.card)!.clippedEnd" name="chevron_right" size="14px" class="gantt-clip is-end" data-testid="gantt-clip-end" />
                      <q-tooltip>
                        <div class="text-weight-medium">{{ line.card.title }}</div>
                        <div>{{ dates(line.card) }}</div>
                        <div>{{ assigneeLabel(line.card) }}</div>
                        <div v-if="progressText(line.card)">{{ progressText(line.card) }}</div>
                      </q-tooltip>
                    </button>
                    <button
                      v-else type="button" class="gantt-milestone" :class="[barClass(line.card), dragClass(line.card)]"
                      :style="{ left: pct(geometry(line.card)!.left + geometry(line.card)!.width / 2) }"
                      :aria-label="barLabel(line.card)" :data-testid="`gantt-milestone-${line.card.id}`" @pointerdown="onDragStart($event, line.card, 'milestone')" @click="onBar(line.card)"
                    >
                      <q-tooltip>
                        <div class="text-weight-medium">{{ line.card.title }}</div>
                        <div>{{ dates(line.card) }}</div>
                        <div>{{ assigneeLabel(line.card) }}</div>
                      </q-tooltip>
                    </button>
                  </template>
                </div>
              </template>
            </div>
          </div>
        </div>
        <section v-if="data.unscheduledTotal > 0" class="q-mt-md" :aria-label="t('taskGantt.unscheduled', { n: data.unscheduledTotal })" data-testid="gantt-unscheduled">
          <div class="text-subtitle2 q-mb-xs" data-testid="gantt-unscheduled-title">{{ t('taskGantt.unscheduled', { n: data.unscheduledTotal }) }}</div>
          <q-list dense bordered separator class="rounded-borders">
            <q-item
              v-for="c in data.unscheduled" :key="c.id" clickable :class="dragClass(c)" :data-testid="`gantt-unscheduled-${c.id}`"
              @pointerdown="(e: PointerEvent) => { if (!(e.target as Element | null)?.closest?.('.gantt-schedule-btn')) onDragStart(e, c, 'unscheduled'); }" @click="emit('open', c.id)"
            >
              <!-- 拖拉把手：把這一列拖到時間軸上某一天，開始日與到期日都設成那天（§11.5.4.6） -->
              <q-item-section v-if="draggable(c)" side class="gantt-grip">
                <span class="row items-center" role="img" :aria-label="t('taskGantt.drag.unscheduledHandle')" :data-testid="`gantt-grip-${c.id}`"><q-icon name="drag_indicator" size="18px" /></span>
              </q-item-section>
              <q-item-section>
                <q-item-label class="gantt-unscheduled-title" :class="{ 'text-strike text-muted': closed(c) }">{{ c.title }}</q-item-label>
                <q-item-label caption class="row items-center q-gutter-x-sm">
                  <span v-if="c.startDate" :data-testid="`gantt-unscheduled-start-${c.id}`">{{ t('taskGantt.startOnly', { date: shortDate(c.startDate) }) }}</span>
                  <span>{{ assigneeLabel(c) }}</span>
                  <q-badge outline :color="taskStatusColor(c.status)" :label="t(`tasks.status.${c.status}`)" />
                </q-item-label>
              </q-item-section>
              <q-item-section v-if="canSchedule(c)" side>
                <q-btn
                  flat dense no-caps color="primary" class="gantt-schedule-btn" :label="t('taskGantt.schedule.button')" :aria-label="t('taskGantt.schedule.buttonLabel', { title: c.title })"
                  :data-testid="`gantt-schedule-${c.id}`" @click.stop="openDates(c)"
                />
              </q-item-section>
            </q-item>
          </q-list>
          <div v-if="data.unscheduledTruncated" class="text-caption text-muted q-mt-xs" role="status" data-testid="gantt-unscheduled-more">
            {{ t('taskGantt.unscheduledMore', { n: unscheduledMore }) }}
          </div>
        </section>
      </template>
    </template>
    <!-- 拖拉中在指標旁顯示新日期（§11.5.4.8）；未排程的列另顯示標題 -->
    <div
      v-if="dragTip" class="gantt-drag-tip rounded-borders text-caption" :style="{ left: `${dragTip.x + 12}px`, top: `${dragTip.y + 12}px` }" aria-hidden="true"
      data-testid="gantt-drag-tip"
    >
      <div v-if="dragTip.title" class="text-weight-medium ellipsis">{{ dragTip.title }}</div>
      <div v-if="dragTip.text" data-testid="gantt-drag-tip-text">{{ dragTip.text }}</div>
    </div>
    <GanttDateDialog v-model="datesOpen" :card="datesCard" :save="scheduler.schedule" />
  </div>
</template>

<style scoped>
.gantt {
  position: relative;
  border: 1px solid var(--t-border);
  background: var(--t-panel);
  overflow: hidden;
}
.gantt.is-busy { opacity: 0.6; }
.gantt-row {
  display: grid;
  grid-template-columns: var(--gantt-left) minmax(0, 1fr);
}
.gantt-line { height: 36px; border-top: 1px solid var(--t-border); }
.gantt-head { min-height: 44px; }
.gantt-head.is-month { min-height: 28px; }
.gantt-left {
  display: flex;
  align-items: center;
  gap: 4px;
  min-width: 0;
  padding: 0 8px;
  border-right: 1px solid var(--t-border);
}
.gantt-left.is-child { padding-left: 56px; }
.gantt.is-tablet .gantt-left.is-child { padding-left: 32px; }
.gantt-edit { flex: 0 0 auto; }
.gantt-weeks.is-busy { opacity: 0.6; }
.gantt-two-lines {
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
  overflow-wrap: anywhere;
}
.gantt-toggle-space { flex: 0 0 24px; }
.gantt-group { background: var(--t-selected-bg); }
.gantt-title {
  flex: 1 1 auto;
  min-width: 0;
  padding: 0;
  border: 0;
  background: none;
  color: inherit;
  font: inherit;
  font-size: 13px;
  line-height: 16px;
  text-align: left;
  cursor: pointer;
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
  overflow-wrap: anywhere;
}
.gantt-title.is-faded { color: var(--t-muted); font-style: italic; }
.gantt-title.is-static { cursor: default; }
.gantt-title:focus-visible, .gantt-bar:focus-visible, .gantt-milestone:focus-visible, .gantt-outside:focus-visible {
  outline: 2px solid var(--t-primary);
  outline-offset: 1px;
}
.gantt-meta { flex: 0 0 auto; display: flex; flex-direction: column; align-items: flex-end; gap: 2px; }
.gantt-axis, .gantt-track { position: relative; min-width: 0; overflow: hidden; }
.gantt-axis-row { position: relative; height: 22px; }
.gantt-head.is-month .gantt-axis-row { height: 28px; }
.gantt-tick {
  position: absolute;
  top: 0;
  bottom: 0;
  padding: 3px 4px 0;
  font-size: 12px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  border-left: 1px solid var(--t-border);
}
.gantt-today-label {
  position: absolute;
  bottom: 0;
  transform: translateX(-50%);
  padding: 0 4px;
  border-radius: 4px;
  background: var(--t-primary);
  color: var(--t-on-primary);
  line-height: 16px;
  z-index: 1;
}
.gantt-body { position: relative; }
.gantt-overlay {
  position: absolute;
  top: 0;
  bottom: 0;
  left: var(--gantt-left);
  right: 0;
  pointer-events: none;
}
.gantt-gridline { position: absolute; top: 0; bottom: 0; border-left: 1px solid var(--t-border); }
.gantt-today-line { position: absolute; top: 0; bottom: 0; width: 2px; margin-left: -1px; background: var(--t-primary); z-index: 2; }
.gantt-bar, .gantt-milestone, .gantt-outside {
  position: absolute;
  padding: 0;
  border: 0;
  cursor: pointer;
  background: var(--t-primary);
  color: var(--t-on-primary);
}
.gantt-bar {
  top: 9px;
  height: 14px;
  min-width: 4px;
  border-radius: 3px;
  overflow: hidden;
}
.gantt-milestone {
  top: 9px;
  width: 14px;
  height: 14px;
  margin-left: -7px;
  transform: rotate(45deg);
  border-radius: 2px;
}
.gantt-outside {
  top: 8px;
  height: 18px;
  width: 18px;
  border-radius: 9px;
  display: flex;
  align-items: center;
  justify-content: center;
}
.gantt-outside.is-before { left: 2px; }
.gantt-outside.is-after { right: 2px; }
.is-overdue { background: var(--t-negative); color: var(--t-on-negative); }
.is-closed { background: var(--t-muted); color: var(--t-panel); }
.gantt-clip { position: absolute; top: 0; }
.gantt-clip.is-start { left: 0; }
.gantt-clip.is-end { right: 0; }
.gantt-span {
  position: absolute;
  top: 26px;
  height: 5px;
  border-radius: 3px;
  background: var(--t-primary);
  opacity: 0.35;
}
.gantt-span.is-faded-row { top: 15px; }
.gantt-empty { position: relative; }
.gantt-unscheduled-title {
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
  overflow-wrap: anywhere;
}
.gantt-handle {
  position: absolute;
  top: 0;
  bottom: 0;
  width: 6px;
  max-width: 30%;
  cursor: ew-resize;
}
.gantt-handle.is-start { left: 0; }
.gantt-handle.is-end { right: 0; }
.gantt-bar.is-draggable, .gantt-milestone.is-draggable { cursor: grab; touch-action: none; }
.gantt-bar.is-dragging, .gantt-milestone.is-dragging { outline: 2px solid var(--t-text); outline-offset: 1px; z-index: 3; }
/* 不能改日期：沒有把手，按下時游標顯示禁止（平常仍可點開任務詳細，同看板） */
.is-locked:active { cursor: not-allowed; }
.gantt-grip { padding-right: 4px; color: var(--t-muted); cursor: grab; }
.task-gantt.is-dragging { user-select: none; }
.task-gantt.is-dragging, .task-gantt.is-dragging * { cursor: grabbing; }
.task-gantt.is-dragging-start, .task-gantt.is-dragging-start *, .task-gantt.is-dragging-due, .task-gantt.is-dragging-due * { cursor: ew-resize; }
.gantt-drag-tip {
  position: fixed;
  z-index: 6000;
  max-width: 260px;
  padding: 4px 8px;
  border: 1px solid var(--t-primary);
  background: var(--t-panel);
  color: var(--t-text);
  white-space: nowrap;
  pointer-events: none;
}
.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
  white-space: nowrap;
}
</style>
