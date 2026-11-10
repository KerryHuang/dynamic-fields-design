import { completeMap } from './fill';
import type { Conversion, ConversionDefinition, ConversionLineStatus, ConvertBody, I18nText } from './types';

export type QuantityError = 'invalid' | 'negative' | 'exceeds';

/** Lines start at what is left (a negative remainder, from a source edited later, starts at 0). */
export function defaultQuantities(lines: ConversionLineStatus[]): Record<number, number> {
  return Object.fromEntries(lines.map((l) => [l.lineId, Math.max(0, l.remaining)]));
}

/** null means fine; 0 is fine (the line is not converted). A blank or NaN input is invalid. */
export function quantityError(quantity: unknown, remaining: number): QuantityError | null {
  if (typeof quantity !== 'number' || !Number.isFinite(quantity)) return 'invalid';
  if (quantity < 0) return 'negative';
  return quantity > Math.max(0, remaining) ? 'exceeds' : null;
}

export function quantityErrors(lines: ConversionLineStatus[], quantities: Record<number, unknown>): Map<number, QuantityError> {
  const out = new Map<number, QuantityError>();
  for (const l of lines) {
    const e = quantityError(quantities[l.lineId], l.remaining);
    if (e) out.set(l.lineId, e);
  }
  return out;
}

/** At least one line above 0 and no line in error. */
export function canConvert(lines: ConversionLineStatus[], quantities: Record<number, unknown>): boolean {
  return quantityErrors(lines, quantities).size === 0 && lines.some((l) => (quantities[l.lineId] as number) > 0);
}

/** Request lines; the order is what the server's `lines[i]` error keys index into. */
export function convertLines(lines: ConversionLineStatus[], quantities: Record<number, number>): NonNullable<ConvertBody['lines']> {
  return lines.filter((l) => quantities[l.lineId]! > 0).map((l) => ({ sourceLineId: l.lineId, quantity: quantities[l.lineId]! }));
}

/** Server keys `lines[i].quantity` back to the source line the i-th sent line came from. */
export function serverQuantityErrors(
  errors: Record<string, string[]> | undefined, sent: NonNullable<ConvertBody['lines']>,
): Map<number, string[]> {
  const out = new Map<number, string[]>();
  for (const [key, messages] of Object.entries(errors ?? {})) {
    const m = /^lines\[(\d+)\]\.quantity$/.exec(key);
    const line = m ? sent[Number(m[1])] : undefined;
    if (line) out.set(line.sourceLineId, messages);
  }
  return out;
}

// ---- designer draft ----

export interface MapRow { from: number | null; to: number | null }

export interface ConversionDraft {
  key: string;
  name: I18nText;
  sourceModuleKey: string | null;
  targetModuleKey: string | null;
  active: boolean;
  sourceStates: number[];
  headerSourceFieldId: number | null;
  headerCopy: MapRow[];
  withLines: boolean;
  linesSourceModuleKey: string | null;
  linesTargetModuleKey: string | null;
  sourceLineFieldId: number | null;
  quantityFrom: number | null;
  quantityTo: number | null;
  linesCopy: MapRow[];
}

export const emptyConversionDraft = (): ConversionDraft => ({
  key: '', name: {}, sourceModuleKey: null, targetModuleKey: null, active: true, sourceStates: [],
  headerSourceFieldId: null, headerCopy: [], withLines: false, linesSourceModuleKey: null, linesTargetModuleKey: null,
  sourceLineFieldId: null, quantityFrom: null, quantityTo: null, linesCopy: [],
});

export function draftFromConversion(c: Conversion): ConversionDraft {
  const d = c.definition;
  const l = d.lines;
  return {
    key: c.key, name: { ...c.name }, sourceModuleKey: c.sourceModuleKey, targetModuleKey: c.targetModuleKey, active: c.active,
    sourceStates: [...d.sourceStates], headerSourceFieldId: d.header.sourceFieldId, headerCopy: d.header.copy.map((m) => ({ ...m })),
    withLines: !!l, linesSourceModuleKey: l?.sourceModuleKey ?? null, linesTargetModuleKey: l?.targetModuleKey ?? null,
    sourceLineFieldId: l?.sourceLineFieldId ?? null, quantityFrom: l?.quantity.from ?? null, quantityTo: l?.quantity.to ?? null,
    linesCopy: l ? l.copy.map((m) => ({ ...m })) : [],
  };
}

/** Request body, or undefined while a required pick is missing (the server validates the rest and its messages are shown as they come). */
export function buildConversion(d: ConversionDraft): Conversion | undefined {
  if (!d.key.trim() || !d.sourceModuleKey || !d.targetModuleKey || d.headerSourceFieldId == null) return undefined;
  let lines: ConversionDefinition['lines'];
  if (d.withLines) {
    if (!d.linesSourceModuleKey || !d.linesTargetModuleKey || d.sourceLineFieldId == null || d.quantityFrom == null || d.quantityTo == null) return undefined;
    lines = {
      sourceModuleKey: d.linesSourceModuleKey, targetModuleKey: d.linesTargetModuleKey, sourceLineFieldId: d.sourceLineFieldId,
      quantity: { from: d.quantityFrom, to: d.quantityTo }, copy: completeMap(d.linesCopy),
    };
  }
  return {
    key: d.key.trim(), name: d.name, sourceModuleKey: d.sourceModuleKey, targetModuleKey: d.targetModuleKey, active: d.active,
    definition: {
      sourceStates: [...d.sourceStates],
      header: { sourceFieldId: d.headerSourceFieldId, copy: completeMap(d.headerCopy) },
      ...(lines && { lines }),
    },
  };
}
