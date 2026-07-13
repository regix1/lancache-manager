import type { CorruptionDetectionMethod, CorruptionScanCoverage } from '@/types';

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

export const isCorruptionDetectionMethod = (value: unknown): value is CorruptionDetectionMethod =>
  value === 'repeated_miss' || value === 'structural';

export const isScanId = (value: unknown): value is string =>
  typeof value === 'string' &&
  /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value);

export const isCountMap = (value: unknown): value is Record<string, number> =>
  value !== null &&
  typeof value === 'object' &&
  !Array.isArray(value) &&
  Object.entries(value).every(
    ([key, count]) =>
      key.trim().length > 0 && typeof count === 'number' && Number.isInteger(count) && count >= 0
  );

export const isCoverage = (value: unknown): value is CorruptionScanCoverage => {
  if (!isPlainRecord(value)) return false;
  const allowedKeys = [
    'filesSeen',
    'filesChecked',
    'consistent',
    'bytesRead',
    'sparseFiles',
    'skippedByReason',
    'ioErrors'
  ] as const;
  if (!hasOnlyKeys(value, allowedKeys) || !isCountMap(value.skippedByReason)) return false;
  const counts = [
    value.filesSeen,
    value.filesChecked,
    value.consistent,
    value.bytesRead,
    value.sparseFiles,
    value.ioErrors
  ];
  return (
    counts.every((count) => typeof count === 'number' && Number.isInteger(count) && count >= 0) &&
    (value.filesChecked as number) <= (value.filesSeen as number) &&
    (value.consistent as number) <= (value.filesChecked as number) &&
    (value.sparseFiles as number) <= (value.filesSeen as number)
  );
};
