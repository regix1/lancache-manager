/** The draft without the entry for `id` when it still holds the value that was sent. */
export const withoutSent = <T extends object, K extends keyof T>(
  draft: Partial<T>,
  id: K,
  sent: T[K]
): Partial<T> => {
  if (draft[id] !== sent) return draft;
  const rest = { ...draft };
  delete rest[id];
  return rest;
};
