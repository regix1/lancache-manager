/**
 * The name a new schedule can take without colliding with the ones a platform already has.
 *
 * The server rejects the whole config when two schedules under one platform share a name, and it
 * compares them case-insensitively after trimming, so "Default copy" and " default COPY " count as
 * the same name here too. A taken base name gets the first free counting suffix: "Default copy 2",
 * then "Default copy 3", and so on.
 */
export const uniqueScheduleName = (baseName: string, existingNames: readonly string[]): string => {
  const taken = new Set(existingNames.map((name) => name.trim().toLowerCase()));

  if (!taken.has(baseName.trim().toLowerCase())) {
    return baseName;
  }

  let suffix = 2;
  while (taken.has(`${baseName} ${suffix}`.trim().toLowerCase())) {
    suffix += 1;
  }

  return `${baseName} ${suffix}`;
};
