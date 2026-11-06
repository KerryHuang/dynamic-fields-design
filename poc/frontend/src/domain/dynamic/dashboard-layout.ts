import type { CardOptions, DashboardCard, DashboardLayout } from './dashboard-types';
import { DEFAULT_CARD_OPTIONS } from './dashboard-types';
import type { ReportViz } from './report-types';

export const GRID_COLUMNS = 12;
export const MAX_CARDS = 40;

const DEFAULT_SIZE: Record<ReportViz, { w: number; h: number }> = {
  indicator: { w: 4, h: 2 },
  table: { w: 6, h: 4 },
  line: { w: 6, h: 4 },
  bar: { w: 6, h: 4 },
  donut: { w: 4, h: 4 },
};

/** 新卡片的 id：不與現有的重複。 */
export function nextCardId(layout: DashboardLayout): string {
  const used = new Set(layout.cards.map((c) => c.id));
  let n = layout.cards.length + 1;
  while (used.has(`c${n}`)) n++;
  return `c${n}`;
}

/** 新卡片放在版面最下面，靠左。 */
export function addCard(layout: DashboardLayout, reportId: number, viz: ReportViz): DashboardLayout {
  const bottom = layout.cards.reduce((m, c) => Math.max(m, c.y + c.h), 0);
  const size = DEFAULT_SIZE[viz];
  const card: DashboardCard = {
    id: nextCardId(layout), reportId, title: null, viz, x: 0, y: bottom, w: size.w, h: size.h, options: { ...DEFAULT_CARD_OPTIONS },
  };
  return { cards: [...layout.cards, card] };
}

export function removeCard(layout: DashboardLayout, cardId: string): DashboardLayout {
  return { cards: layout.cards.filter((c) => c.id !== cardId) };
}

export function updateCard(layout: DashboardLayout, cardId: string, change: Partial<Pick<DashboardCard, 'title' | 'viz'>> & { options?: CardOptions }): DashboardLayout {
  return { cards: layout.cards.map((c) => (c.id === cardId ? { ...c, ...change } : c)) };
}

/** 拖放或調整大小後的位置寫回卡片；沒有在 positions 裡的卡片不動。 */
export function applyPositions(layout: DashboardLayout, positions: { i: string; x: number; y: number; w: number; h: number }[]): DashboardLayout {
  const byId = new Map(positions.map((p) => [p.i, p]));
  return { cards: layout.cards.map((c) => { const p = byId.get(c.id); return p ? { ...c, x: p.x, y: p.y, w: p.w, h: p.h } : c; }) };
}

/** 窄螢幕單欄排列的順序：由上到下、同列由左到右。 */
export function readingOrder(cards: DashboardCard[]): DashboardCard[] {
  return [...cards].sort((a, b) => a.y - b.y || a.x - b.x);
}

/** 版面內容相同（忽略卡片順序）。 */
export function sameLayout(a: DashboardLayout, b: DashboardLayout): boolean {
  return JSON.stringify(readingOrder(a.cards)) === JSON.stringify(readingOrder(b.cards));
}

/** 日期篩選的預設選項（暫時、不存檔）；kind 對應後端的相對區間。 */
export const FILTER_PRESETS: { key: string; kind: string; n?: number }[] = [
  { key: 'thisMonth', kind: 'thisMonth' },
  { key: 'lastMonth', kind: 'lastMonth' },
  { key: 'thisWeek', kind: 'thisWeek' },
  { key: 'last30', kind: 'lastDays', n: 30 },
  { key: 'last90', kind: 'lastDays', n: 90 },
  { key: 'thisYear', kind: 'thisYear' },
];
