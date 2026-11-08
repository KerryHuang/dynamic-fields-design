/** One `Idempotency-Key` per user intention: made when a form or dialog opens, kept on retries, replaced after a success. */
export const newIdempotencyKey = (): string => crypto.randomUUID();
