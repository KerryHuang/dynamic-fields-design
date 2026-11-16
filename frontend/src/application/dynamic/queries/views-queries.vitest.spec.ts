import { describe, expect, it } from 'vitest';
import type { ListView } from 'src/domain/dynamic/types';
import { pickDefaultView } from './views-queries';

const view = (id: number, p: Partial<ListView>): ListView => ({
  id, name: { 'zh-TW': `v${id}` }, shared: false, isOwner: true, columns: [1], sort: [], isDefault: false, staleFieldIds: [], ...p,
});

describe('pickDefaultView', () => {
  it('prefers own default, then shared default', () => {
    const sharedDefault = view(1, { shared: true, isDefault: true });
    const ownDefault = view(2, { isDefault: true });
    expect(pickDefaultView([sharedDefault, ownDefault, view(3, {})])?.id).toBe(2);
    expect(pickDefaultView([view(3, {}), sharedDefault])?.id).toBe(1);
    expect(pickDefaultView([view(3, {})])).toBeUndefined();
  });
});
