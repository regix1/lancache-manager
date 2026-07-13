import { projectCorruptionCounts } from './corruptionCountProjection';
import {
  hasOnlyKeys,
  isCorruptionDetectionMethod,
  isCountMap,
  isCoverage,
  isIsoDate,
  isNonNegativeInteger,
  isPlainRecord,
  isScanId
} from './corruptionContractValidation';
import type {
  CorruptedChunkDetail,
  CorruptionDetectionMethod,
  CorruptionScanHistoryEntry
} from '@/types';
import type { StructuralScanMode } from '@/types/corruptionScan';

const HISTORY_ENTRY_KEYS = [
  'scanId',
  'detectionMethod',
  'isCurrent',
  'completedAtUtc',
  'settings',
  'contractVersion',
  'corruptionCounts',
  'detectionCounts',
  'coverage',
  'totalServicesWithCorruption',
  'totalCorruptedChunks',
  'scanMode'
] as const;

const SETTINGS_KEYS = [
  'threshold',
  'lookbackDays',
  'minStableAgeSeconds',
  'maxPrefixBytes'
] as const;

const isStructuralScanMode = (value: unknown): value is StructuralScanMode =>
  value === 'full' || value === 'incremental';

const isHistoryEntry = (value: unknown): value is CorruptionScanHistoryEntry => {
  if (!isPlainRecord(value) || !hasOnlyKeys(value, HISTORY_ENTRY_KEYS)) return false;
  if (!isScanId(value.scanId) || value.contractVersion !== 4) return false;
  if (!isCorruptionDetectionMethod(value.detectionMethod)) return false;
  if (typeof value.isCurrent !== 'boolean' || !isIsoDate(value.completedAtUtc)) return false;
  if (!isCountMap(value.corruptionCounts) || !isCountMap(value.detectionCounts)) return false;
  if (
    !isNonNegativeInteger(value.totalServicesWithCorruption) ||
    !isNonNegativeInteger(value.totalCorruptedChunks)
  ) {
    return false;
  }

  const method = value.detectionMethod;
  const settings = value.settings;
  if (!isPlainRecord(settings) || !hasOnlyKeys(settings, SETTINGS_KEYS)) return false;
  if (method === 'repeated_miss') {
    if (typeof settings.threshold !== 'number' || ![3, 5, 10].includes(settings.threshold)) {
      return false;
    }
    if (
      typeof settings.lookbackDays !== 'number' ||
      !Number.isInteger(settings.lookbackDays) ||
      settings.lookbackDays < 1 ||
      settings.lookbackDays > 365
    ) {
      return false;
    }
    if (settings.minStableAgeSeconds != null || settings.maxPrefixBytes != null) return false;
    // Repeated MISS scans never carry a structural scan mode or coverage.
    if (value.scanMode != null || value.coverage != null) return false;
  } else {
    if (settings.threshold != null || settings.lookbackDays != null) return false;
    if (settings.minStableAgeSeconds !== 600 || settings.maxPrefixBytes !== 65_535) return false;
    // Legacy Structural snapshots may have an unknown (null) mode; never invent one.
    if (value.scanMode != null && !isStructuralScanMode(value.scanMode)) return false;
    if (value.coverage != null && !isCoverage(value.coverage)) return false;
  }

  const detectionCounts = value.detectionCounts;
  if (!Object.keys(detectionCounts).every((key) => key === method)) return false;
  if (detectionCounts[method] !== value.totalCorruptedChunks) return false;

  const projection = projectCorruptionCounts(value.corruptionCounts);
  return (
    projection.isConsistent &&
    projection.serviceTotal === value.totalServicesWithCorruption &&
    projection.total === value.totalCorruptedChunks
  );
};

/**
 * Validates the closed scan-history contract: at most three snapshots per
 * detection method, unique scan IDs, and at most one explicit current per
 * method. Returns null (fail closed) on any violation; never coerces.
 */
export const validateCorruptionScanHistory = (
  value: unknown
): CorruptionScanHistoryEntry[] | null => {
  if (!isPlainRecord(value) || !hasOnlyKeys(value, ['scans']) || !Array.isArray(value.scans)) {
    return null;
  }
  const scans: unknown[] = value.scans;
  if (!scans.every(isHistoryEntry)) return null;
  const entries = scans as CorruptionScanHistoryEntry[];

  const perMethod: Record<CorruptionDetectionMethod, number> = { repeated_miss: 0, structural: 0 };
  const currentPerMethod: Record<CorruptionDetectionMethod, number> = {
    repeated_miss: 0,
    structural: 0
  };
  const scanIds = new Set<string>();
  for (const entry of entries) {
    if (scanIds.has(entry.scanId)) return null;
    scanIds.add(entry.scanId);
    perMethod[entry.detectionMethod] += 1;
    if (entry.isCurrent) currentPerMethod[entry.detectionMethod] += 1;
  }
  if (perMethod.repeated_miss > 3 || perMethod.structural > 3) return null;
  if (currentPerMethod.repeated_miss > 1 || currentPerMethod.structural > 1) return null;
  return entries;
};

/**
 * Shallow method-aware check for read-only history evidence. Deep per-candidate
 * evidence validation stays in CorruptionChunkList, which rejects unsafe items
 * before rendering.
 */
export const validateCorruptionHistoryDetails = (
  value: unknown,
  expectedMethod: CorruptionDetectionMethod
): CorruptedChunkDetail[] | null => {
  if (!Array.isArray(value)) return null;
  const valid = value.every(
    (item) =>
      isPlainRecord(item) && isPlainRecord(item.evidence) && item.evidence.kind === expectedMethod
  );
  return valid ? (value as CorruptedChunkDetail[]) : null;
};
