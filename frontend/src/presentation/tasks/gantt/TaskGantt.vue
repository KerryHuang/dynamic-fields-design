<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useGantt } from 'src/application/dynamic/queries/task-board-queries';
import type { BoardCard, GanttSource, TaskGanttConfig, TaskGanttWindow } from 'src/domain/dynamic/task-board-types';
import {
  barGeometry, formatGanttDate, ganttLines, GANTT_LEFT_WIDTH, minorTicks, monthTicks, shiftWindow, spanGeometry, todayPosition, windowFor,
  type GanttLine, type GanttScale,
} from 'src/domain/dynamic/task-gantt';
import { progressView, taskStatusColor } from 'src/domain/dynamic/task-types';
import SubtaskProgress from 'src/presentation/dynamic/tasks/SubtaskProgress.vue';

/**
 * 甘特圖（看板設計 §11.5.4，gt2a 唯讀）：預設甘特圖、刻度與平移、三種分組、子任務跨度、今天線、未排程。
 * 時間軸用 CSS grid 與百分比定位自畫（GT-T21），寬度填滿容器、不橫向捲動；用 Quasar 視窗斷點判斷寬度（GT-T17）。
 * 平板（< 1024 或觸控）與手機（< 600）在 gt2a 只顯示說明（§11.5.8 拆分備案），日期對話框與手機週清單隨 gt2b。
 */
const emit = defineEmits<{ open: [id: number]; list: [] }>();
const { t, locale } = useI18n();
const $q = useQuasar();

// 觸控（主要指標是手指）一律當平板處理，與寬度無關（§11.5.4.2）
const coarseQuery = typeof window !== 'undefined' && typeof window.matchMedia === 'function' ? window.matchMedia('(pointer: coarse)') : null;
const coarse = ref(!!coarseQuery?.matches);
const onPointerChange = () => { coarse.value = !!coarseQuery?.matches; };
coarseQuery?.addEventListener?.('change', onPointerChange);
onBeforeUnmount(() => coarseQuery?.removeEventListener?.('change', onPointerChange));
const wide = computed(() => !$q.screen.lt.md && !coarse.value);

// 刻度與時間窗由使用者在畫面上切換；沒切過就照伺服器（預設甘特圖的刻度、伺服器算的預設時間窗）。
// 換了刻度後「今天」要用新刻度的預設時間窗，所以把刻度放進設定一起送（只改刻度，其餘是伺服器回的設定）。
const scale = ref<GanttScale | null>(null);
const win = ref<TaskGanttWindow | null>(null);
const base = ref<TaskGanttConfig | null>(null);
const source = computed<GanttSource>(() => ({
  viewId: null,
  config: scale.value && base.value && scale.value !== base.value.scale ? { ...base.value, scale: scale.value } : null,
  window: win.value,
}));
const query = useGantt(source, wide);
const data = computed(() => query.data.value ?? null);
watch(data, (d) => { if (d && !base.value) base.value = d.config; }, { immediate: true });
const shownScale = computed<GanttScale>(() => scale.value ?? data.value?.config.scale ?? 'week');
// 上一段還沒換成新的一段前不能再平移（平移以畫面上的時間窗為起點）
const busy = computed(() => query.isPlaceholderData.value);

function shift(dir: 1 | -1) {
  if (data.value) win.value = shiftWindow(data.value.window, shownScale.value, dir);
}
function setScale(s: GanttScale) {
  scale.value = s;
  if (data.value) win.value = windowFor(s, data.value.window.from, data.value.weekStart);
}
const goToday = () => { win.value = null; };
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
const geometry = (c: BoardCard) => barGeometry(c.startDate, c.dueDate, windowOf.value);
const spanOf = (l: { span: { parentId: number; from: string; to: string } | null }) => spanGeometry(l.span, windowOf.value);
const unscheduledMore = computed(() => (data.value ? data.value.unscheduledTotal - data.value.unscheduled.length : 0));
const shortDate = (iso: string) => formatGanttDate(iso, locale.value, { month: 'numeric', day: 'numeric' });
</script>

<template>
  <div class="task-gantt" role="region" :aria-label="t('taskGantt.region')" data-testid="gantt">
    <q-banner v-if="!wide" dense rounded class="bg-info-soft" role="status" data-testid="gantt-narrow">
      {{ t('taskGantt.narrow') }}
      <template #action><q-btn flat dense no-caps color="primary" :label="t('taskGantt.useList')" data-testid="gantt-use-list" @click="emit('list')" /></template>
    </q-banner>
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
        <div class="gantt" :class="{ 'is-busy': busy }" :aria-busy="busy" :style="{ '--gantt-left': `${GANTT_LEFT_WIDTH}px` }" data-testid="gantt-chart">
          <div class="gantt-row gantt-head" :class="`is-${shownScale}`">
            <div class="gantt-left text-caption text-muted">{{ t('taskGantt.taskColumn') }}</div>
            <div class="gantt-axis" aria-hidden="true">
              <div class="gantt-axis-row">
                <span v-for="m in majors" :key="m.date" class="gantt-tick" :style="{ left: pct(m.left), width: pct(m.width) }" data-testid="gantt-major">{{ m.label }}</span>
              </div>
              <div v-if="minors.length" class="gantt-axis-row">
                <span v-for="m in minors" :key="m.date" class="gantt-tick" :style="{ left: pct(m.left), width: pct(m.width) }" data-testid="gantt-minor">{{ m.label }}</span>
              </div>
              <span v-if="today !== null" class="gantt-today-label text-caption" :style="{ left: pct(today) }" data-testid="gantt-today-label">{{ t('taskGantt.todayLine') }}</span>
            </div>
          </div>
          <div class="gantt-body">
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
                      :aria-label="barLabel(line.card)" :data-testid="`gantt-outside-${line.card.id}`" @click="emit('open', line.card.id)"
                    >
                      <q-icon :name="geometry(line.card)!.outside === 'before' ? 'chevron_left' : 'chevron_right'" size="16px" />
                      <q-tooltip>{{ line.card.title }}<br>{{ dates(line.card) }}</q-tooltip>
                    </button>
                    <button
                      v-else-if="geometry(line.card)!.kind === 'bar'" type="button" class="gantt-bar" :class="barClass(line.card)"
                      :style="{ left: pct(geometry(line.card)!.left), width: pct(geometry(line.card)!.width) }"
                      :aria-label="barLabel(line.card)" :data-testid="`gantt-bar-${line.card.id}`" @click="emit('open', line.card.id)"
                    >
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
                      v-else type="button" class="gantt-milestone" :class="barClass(line.card)" :style="{ left: pct(geometry(line.card)!.left + geometry(line.card)!.width / 2) }"
                      :aria-label="barLabel(line.card)" :data-testid="`gantt-milestone-${line.card.id}`" @click="emit('open', line.card.id)"
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
            <q-item v-for="c in data.unscheduled" :key="c.id" clickable :data-testid="`gantt-unscheduled-${c.id}`" @click="emit('open', c.id)">
              <q-item-section>
                <q-item-label class="gantt-unscheduled-title" :class="{ 'text-strike text-muted': closed(c) }">{{ c.title }}</q-item-label>
                <q-item-label caption class="row items-center q-gutter-x-sm">
                  <span v-if="c.startDate" :data-testid="`gantt-unscheduled-start-${c.id}`">{{ t('taskGantt.startOnly', { date: shortDate(c.startDate) }) }}</span>
                  <span>{{ assigneeLabel(c) }}</span>
                  <q-badge outline :color="taskStatusColor(c.status)" :label="t(`tasks.status.${c.status}`)" />
                </q-item-label>
              </q-item-section>
            </q-item>
          </q-list>
          <div v-if="data.unscheduledTruncated" class="text-caption text-muted q-mt-xs" role="status" data-testid="gantt-unscheduled-more">
            {{ t('taskGantt.unscheduledMore', { n: unscheduledMore }) }}
          </div>
        </section>
      </template>
    </template>
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
.gantt-left.is-child { padding-left: 36px; }
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
.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
  white-space: nowrap;
}
</style>
