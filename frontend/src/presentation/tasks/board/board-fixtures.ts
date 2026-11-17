/** Wire-shaped board payloads (null properties are left out, like the server does). */
export const cardWire = (o: Record<string, unknown> = {}) => ({
  id: 1, title: '回覆恆川機械樣品問題', status: 'todo', assigneeUserId: 'sales01', assigneeName: '業務一', assigneeActive: true,
  dueDate: '2026-12-01', overdue: false, dueToday: false,
  record: { accessible: true, moduleKey: 'crm_customer', recordId: 9, title: '甲公司', deleted: false },
  fromRule: false, frozen: false, commentCount: 0, version: '20261101020000000', canEdit: true, canReassign: true, ...o,
});

export const columnWire = (o: Record<string, unknown> = {}) => ({
  key: 'todo', kind: 'status', total: 1, cards: [cardWire()], hasMore: false, truncated: false, droppable: true, ...o,
});

/** 預設看板：待辦、進行中、已完成三欄（隱藏「取消」）。 */
export const boardWire = (o: Record<string, unknown> = {}) => ({
  today: '2026-11-17', weekEnd: '2026-11-22', laterDefault: '2026-11-29', scopeAvailable: true, omittedAssignees: 0,
  columns: [
    columnWire({ key: 'todo', total: 2, cards: [cardWire({ id: 1 }), cardWire({ id: 2, title: '寄合約', record: null })] }),
    columnWire({ key: 'inProgress', total: 1, cards: [cardWire({ id: 3, title: '整理名片', status: 'inProgress', overdue: true })] }),
    columnWire({ key: 'done', total: 0, cards: [] }),
  ],
  ...o,
});

/** 依負責人分欄：未指派欄與兩位使用者（其中一位已停用，不能放）。 */
export const assigneeBoardWire = () => boardWire({
  columns: [
    columnWire({ key: 'unassigned', kind: 'unassigned', total: 0, cards: [] }),
    columnWire({ key: 'user:sales01', kind: 'user', userId: 'sales01', name: '業務一', inactive: false, total: 1, cards: [cardWire({ id: 1 })] }),
    columnWire({ key: 'user:old01', kind: 'user', userId: 'old01', name: '老王', inactive: true, total: 0, cards: [], droppable: false }),
  ],
});

/** 到期區間分欄；今天是本週最後一天時「本週」欄不能放。 */
export const dueBoardWire = () => boardWire({
  columns: ['overdue', 'today', 'thisWeek', 'later', 'none'].map((key) => columnWire({
    key, kind: 'dueBucket', total: 0, cards: [],
    ...(key === 'overdue' ? { droppable: false } : {}),
  })),
});

export const nineStatusLikeColumns = () => Array.from({ length: 9 }, (_, i) => columnWire({
  key: `user:u${i + 1}`, kind: 'user', userId: `u${i + 1}`, name: `同事${i + 1}`, inactive: false, total: i, cards: [],
}));
