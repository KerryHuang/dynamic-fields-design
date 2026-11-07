import type { InjectionKey } from 'vue';

/**
 * Provided by the record form in edit mode. An upload is saved on its own and bumps the record's rowVersion,
 * so the form must send the new one on its next save or it would hit a false 409.
 */
export interface AttachmentContext {
  recordId: number | null;
  onUploaded(rowVersion: string): void;
}

export const attachmentContextKey: InjectionKey<AttachmentContext> = Symbol('attachment-context');
