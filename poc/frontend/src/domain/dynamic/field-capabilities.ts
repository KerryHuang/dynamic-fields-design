import type { FieldType } from './types';

/** Mirrors the backend rule: textarea is neither filterable nor sortable; multienum is filterable only. */
export const supportsFilter = (type: FieldType): boolean => type !== 'textarea';
export const supportsSort = (type: FieldType): boolean => type !== 'textarea' && type !== 'multienum';
