import { describe, expect, it } from 'vitest';
import { completeMap, fillPatch, fillReferenceInfos, lockedFillKeys } from './fill';
import type { FillConfig } from './types';

const map = [{ from: 31, to: 12 }, { from: 32, to: 13 }];
const lock: FillConfig = { mode: 'lock', map };
const dflt: FillConfig = { mode: 'default', map };
const source = { f_31: 'Acme', f_32: 'Net 30', f_99: 'ignored' };

describe('fillPatch', () => {
  it('lock overwrites the targets with the source values', () => {
    expect(fillPatch(lock, source, { f_12: 'old', f_13: null })).toEqual({ f_12: 'Acme', f_13: 'Net 30' });
  });
  it('lock clears a target when the source value is missing or hidden', () => {
    expect(fillPatch(lock, { f_31: 'Acme' }, { f_12: 'x', f_13: 'y' })).toEqual({ f_12: 'Acme', f_13: null });
  });
  it('lock clears the targets when the reference is cleared', () => {
    expect(fillPatch(lock, undefined, { f_12: 'x', f_13: 'y' })).toEqual({ f_12: null, f_13: null });
  });
  it('default fills only blank targets', () => {
    expect(fillPatch(dflt, source, { f_12: '', f_13: 'typed' })).toEqual({ f_12: 'Acme' });
  });
  it('default leaves a target alone when the source value is empty', () => {
    expect(fillPatch(dflt, { f_31: null, f_32: '' }, { f_12: null, f_13: null })).toEqual({});
  });
  it('default does not touch anything when the reference is cleared', () => {
    expect(fillPatch(dflt, undefined, { f_12: 'x', f_13: null })).toEqual({});
  });
  it('default also replaces a value the user has not changed from the stored one', () => {
    const baseline = { f_12: 'stored', f_13: 'stored13' };
    expect(fillPatch(dflt, source, { f_12: 'stored', f_13: 'edited' }, baseline)).toEqual({ f_12: 'Acme' });
  });
  it('keeps falsy but real values such as 0 and false', () => {
    expect(fillPatch(dflt, { f_31: 0, f_32: false }, { f_12: null, f_13: null })).toEqual({ f_12: 0, f_13: false });
  });
});

describe('lockedFillKeys / completeMap', () => {
  it('lists only the targets of lock fills', () => {
    expect([...lockedFillKeys([{ config: { fill: lock } }, { config: { fill: { mode: 'default', map: [{ from: 1, to: 77 }] } } }, { config: {} }])])
      .toEqual(['f_12', 'f_13']);
  });
  it('keeps only rows with both sides picked', () => {
    expect(completeMap([{ from: 1, to: 2 }, { from: null, to: 3 }, { from: 4, to: null }])).toEqual([{ from: 1, to: 2 }]);
  });
});

describe('fillReferenceInfos', () => {
  it('re-keys the source reference info to the filled target fields', () => {
    const refs = { f_31: { key: '7', title: 'Seven', state: 'ok' }, f_32: { key: '8', title: 'Eight', state: 'ok' } };
    expect(fillReferenceInfos(lock, { f_12: 7 }, refs)).toEqual({ f_12: refs.f_31 });
    expect(fillReferenceInfos(lock, { f_12: 7, f_13: 8 }, undefined)).toEqual({});
  });
});
