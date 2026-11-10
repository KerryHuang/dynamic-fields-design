import type { MentionRef } from './task-types';

export interface MentionPick {
  userId: string;
  displayName: string;
  /** Text shown after the "@" when it differs from the display name (two people share one name). */
  label?: string;
}

export type MentionSegment =
  | { kind: 'text'; text: string }
  | { kind: 'mention'; userId: string; name: string; active: boolean };

const escapeRegExp = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

/** Same shape the server parses: `@{userId}` with 1-50 of letters, digits, dot, underscore, hyphen. */
const TOKEN = /@\{([A-Za-z0-9._-]{1,50})\}/g;

/** Turns the visible "@display name" of each picked person into the stored "@{userId}". */
export function encodeMentions(text: string, picked: MentionPick[]): string {
  const shown = (p: MentionPick) => p.label || p.displayName;
  const names = picked.filter(shown).sort((a, b) => shown(b).length - shown(a).length);
  if (!names.length) return text;
  const byName = new Map(names.map((p) => [shown(p), p.userId]));
  const pattern = new RegExp(`@(${names.map((p) => escapeRegExp(shown(p))).join('|')})`, 'g');
  return text.replace(pattern, (_m, name: string) => `@{${byName.get(name)}}`);
}

/** Splits a stored body into text and mentions; a token the server did not resolve stays as plain text. */
export function mentionSegments(body: string, mentions: MentionRef[]): MentionSegment[] {
  const known = new Map(mentions.map((m) => [m.userId.toLowerCase(), m]));
  const out: MentionSegment[] = [];
  const pushText = (text: string) => {
    if (!text) return;
    const last = out[out.length - 1];
    if (last?.kind === 'text') last.text += text;
    else out.push({ kind: 'text', text });
  };
  let cursor = 0;
  for (const m of body.matchAll(TOKEN)) {
    const ref = known.get(m[1].toLowerCase());
    if (!ref) continue;
    pushText(body.slice(cursor, m.index));
    out.push({ kind: 'mention', userId: ref.userId, name: ref.displayName, active: ref.active });
    cursor = m.index + m[0].length;
  }
  pushText(body.slice(cursor));
  return out;
}

/** The word typed after the last "@" before the caret, or null when the caret is not inside a mention. */
export function mentionQuery(text: string, caret: number): string | null {
  const before = text.slice(0, caret);
  const at = before.lastIndexOf('@');
  if (at < 0) return null;
  if (at > 0 && !/\s/.test(before[at - 1])) return null;
  const word = before.slice(at + 1);
  return /\s/.test(word) ? null : word;
}
