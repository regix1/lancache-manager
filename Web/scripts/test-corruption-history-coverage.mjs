import assert from 'node:assert/strict';
import test from 'node:test';
import { compileTree } from './transpile-module.mjs';

/**
 * An Incremental Scan counts the files it reuses from its baseline as consistent without checking
 * them again, so its coverage has consistent above filesChecked. The server accepts that
 * (filesChecked + reused = consistent + candidates); the page must too, or one incremental scan
 * hides the whole scan history.
 */

const { validateCorruptionScanHistory } = await import(
  await compileTree('../src/components/features/management/cache/corruptionHistoryValidation.ts')
);

const structuralScan = (coverage) => ({
  scans: [
    {
      scanId: '6f1c2a3b-4d5e-4f60-8a7b-9c0d1e2f3a4b',
      detectionMethod: 'structural',
      isCurrent: true,
      completedAtUtc: '2026-10-04T20:00:00Z',
      settings: { minStableAgeSeconds: 600, maxPrefixBytes: 65_535 },
      contractVersion: 4,
      corruptionCounts: { steam: 1 },
      detectionCounts: { structural: 1 },
      coverage,
      totalServicesWithCorruption: 1,
      totalCorruptedChunks: 1,
      scanMode: 'incremental'
    }
  ]
});

// 1000 files seen: 900 reused from the baseline, 100 checked, 1 of those invalid.
const incrementalCoverage = {
  filesSeen: 1000,
  filesChecked: 100,
  consistent: 999,
  bytesRead: 6_553_500,
  sparseFiles: 0,
  skippedByReason: {},
  ioErrors: 0
};

test('an incremental scan that reused its baseline keeps the scan history', () => {
  assert.notEqual(validateCorruptionScanHistory(structuralScan(incrementalCoverage)), null);
});

test('coverage claiming more consistent files than it saw is still rejected', () => {
  assert.equal(
    validateCorruptionScanHistory(structuralScan({ ...incrementalCoverage, consistent: 1001 })),
    null
  );
});
