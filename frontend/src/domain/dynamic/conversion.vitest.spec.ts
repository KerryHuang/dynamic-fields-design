import { describe, expect, it } from 'vitest';
import {
  buildConversion, canConvert, convertLines, defaultQuantities, draftFromConversion, emptyConversionDraft, quantityError,
  quantityErrors, serverQuantityErrors,
} from './conversion';
import type { Conversion, ConversionLineStatus } from './types';

const line = (lineId: number, quantity: number, converted: number): ConversionLineStatus =>
  ({ lineId, title: `L${lineId}`, quantity, converted, remaining: quantity - converted });
const lines = [line(1, 10, 4), line(2, 5, 5), line(3, 3, 4)];

describe('quantities', () => {
  it('defaults to the remaining quantity, never below 0', () => {
    expect(defaultQuantities(lines)).toEqual({ 1: 6, 2: 0, 3: 0 });
  });
  it('validates against the remaining quantity', () => {
    expect(quantityError(6, 6)).toBeNull();
    expect(quantityError(0, 6)).toBeNull();
    expect(quantityError(6.5, 6)).toBe('exceeds');
    expect(quantityError(-1, 6)).toBe('negative');
    expect(quantityError(null, 6)).toBe('invalid');
    expect(quantityError(Number.NaN, 6)).toBe('invalid');
    expect(quantityError('3', 6)).toBe('invalid');
    expect(quantityError(1, 0)).toBe('exceeds');
  });
  it('maps errors per line', () => {
    expect([...quantityErrors(lines, { 1: 7, 2: 0, 3: 0 })]).toEqual([[1, 'exceeds']]);
  });
  it('can convert when nothing is wrong and at least one line is above 0', () => {
    expect(canConvert(lines, { 1: 6, 2: 0, 3: 0 })).toBe(true);
    expect(canConvert(lines, { 1: 0, 2: 0, 3: 0 })).toBe(false);
    expect(canConvert(lines, { 1: 7, 2: 0, 3: 0 })).toBe(false);
  });
  it('sends only the lines above 0, in order', () => {
    expect(convertLines(lines, { 1: 2, 2: 0, 3: 0 })).toEqual([{ sourceLineId: 1, quantity: 2 }]);
  });
  it('maps server lines[i].quantity back to the source line', () => {
    const sent = [{ sourceLineId: 1, quantity: 2 }, { sourceLineId: 3, quantity: 1 }];
    const out = serverQuantityErrors({ 'lines[1].quantity': ['too many'], 'lines[9].quantity': ['x'], other: ['y'] }, sent);
    expect([...out]).toEqual([[3, ['too many']]]);
  });
});

describe('conversion draft', () => {
  const conversion: Conversion = {
    key: 'quote-to-order', name: { 'zh-TW': 'A 轉 B' }, sourceModuleKey: 'quote', targetModuleKey: 'order', active: true,
    definition: {
      sourceStates: [12],
      header: { sourceFieldId: 80, copy: [{ from: 21, to: 71 }] },
      lines: { sourceModuleKey: 'quoteLine', targetModuleKey: 'orderLine', sourceLineFieldId: 90, quantity: { from: 41, to: 91 }, copy: [{ from: 42, to: 92 }] },
    },
  };

  it('round-trips a saved conversion', () => {
    expect(buildConversion(draftFromConversion(conversion))).toEqual(conversion);
  });
  it('builds a header-only conversion without lines', () => {
    const header: Conversion = { ...conversion, definition: { sourceStates: [], header: conversion.definition.header } };
    const built = buildConversion(draftFromConversion(header))!;
    expect(built.definition).toEqual(header.definition);
    expect('lines' in built.definition).toBe(false);
  });
  it('drops half-filled map rows', () => {
    const d = draftFromConversion(conversion);
    d.headerCopy.push({ from: 5, to: null });
    expect(buildConversion(d)!.definition.header.copy).toEqual([{ from: 21, to: 71 }]);
  });
  it('builds nothing while a required pick is missing', () => {
    expect(buildConversion(emptyConversionDraft())).toBeUndefined();
    expect(buildConversion({ ...draftFromConversion(conversion), sourceLineFieldId: null })).toBeUndefined();
    expect(buildConversion({ ...draftFromConversion(conversion), key: ' ' })).toBeUndefined();
  });
});
