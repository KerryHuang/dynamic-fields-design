import type { LedgerBody } from './events-ledger';
import type { DesignerField, FieldConfig, I18nText, ModuleDetail } from './types';

export interface LedgerDraft {
  enabled: boolean;
  reversalFieldId: number | null;
  negate: number[];
  /** A pair is only sent when both sides are picked. */
  swap: [number | null, number | null][];
}

const NUMBER_TYPES = ['integer', 'decimal', 'currency'];
const active = (fields: DesignerField[]) => fields.filter((f) => f.status === 'Active');

/** Reversal field candidates: read-only reference fields of the module that point at the module itself. */
export function reversalCandidates(module: ModuleDetail): DesignerField[] {
  return active(module.blocks.flatMap((b) => b.fields)).filter((f) =>
    f.type === 'reference' && f.config.readOnly && f.config.refTarget?.kind === 'module' && f.config.refTarget.key === module.key);
}

/** Integer, decimal and currency fields: what a reversal can negate or swap. */
export const numberFields = (fields: DesignerField[]): DesignerField[] => active(fields).filter((f) => NUMBER_TYPES.includes(f.type));

/** Fields a swap can pair with `firstId`: same type, another field. */
export function swapPartners(fields: DesignerField[], firstId: number | null): DesignerField[] {
  const first = numberFields(fields).find((f) => f.id === firstId);
  return first ? numberFields(fields).filter((f) => f.type === first.type && f.id !== first.id) : [];
}

/** Request body, or undefined while the switch is off or the reversal field is not picked. */
export function buildLedgerBody(d: LedgerDraft, confirmFreeze: boolean): LedgerBody | undefined {
  if (!d.enabled || d.reversalFieldId == null) return undefined;
  return {
    reversalFieldId: d.reversalFieldId, negate: [...d.negate],
    swap: d.swap.filter((p): p is [number, number] => p[0] != null && p[1] != null).map((p) => [p[0], p[1]]), confirmFreeze,
  };
}

/** Body for the existing add-field API: a read-only, optional reference to the module itself. */
export function newReversalField(blockId: number, moduleKey: string, label: I18nText) {
  const config: FieldConfig = {
    required: false, readOnly: true, showInList: false, isFilterable: false, isSortable: false,
    refTarget: { kind: 'module', key: moduleKey }, onDelete: 'restrict',
  };
  return { blockId, type: 'reference' as const, label, config };
}
