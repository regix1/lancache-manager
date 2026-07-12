export const isPlainRecord = (value: unknown): value is Record<string, unknown> =>
  value !== null && typeof value === 'object' && !Array.isArray(value);

export const hasOnlyKeys = (value: Record<string, unknown>, allowedKeys: readonly string[]) =>
  Object.keys(value).every((key) => allowedKeys.includes(key));

export const isNonNegativeInteger = (value: unknown): value is number =>
  typeof value === 'number' && Number.isInteger(value) && value >= 0;

export const isOptionalNonNegativeInteger = (value: unknown) =>
  value == null || isNonNegativeInteger(value);

export const isIsoDate = (value: unknown): value is string =>
  typeof value === 'string' && value.trim().length > 0 && !Number.isNaN(Date.parse(value));
