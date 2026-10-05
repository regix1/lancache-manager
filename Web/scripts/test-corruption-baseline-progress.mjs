import assert from 'node:assert/strict';
import test from 'node:test';
import { compileTree, moduleUrl } from './transpile-module.mjs';

/**
 * The scanner reports baselineStatus 'building' for every stateful run until it commits, including
 * an Incremental Scan that found and is reusing a baseline. Only effectiveScanMode says whether a
 * baseline was found, so only it may decide the "no compatible baseline" text.
 */

const { formatCorruptionProgress } = await import(
  await compileTree('../src/contexts/notifications/corruptionProgress.ts', {
    '@/i18n': moduleUrl('export default {t:(key)=>key};')
  })
);

const incrementalCheckpoint = (effectiveScanMode) => ({
  detectionMethod: 'structural',
  stageKey: 'signalr.corruptionDetect.scanningHeaders',
  scanMode: 'incremental',
  effectiveScanMode,
  baselineStatus: 'building',
  filesProcessed: 10,
  totalFiles: 100
});

test('an incremental scan that reuses a baseline reads as incremental while its new baseline builds', () => {
  const progress = formatCorruptionProgress(incrementalCheckpoint('incremental'));
  assert.equal(progress.message, 'signalr.corruptionDetect.scanningIncremental');
});

test('an incremental scan that found no baseline reads as building one', () => {
  const progress = formatCorruptionProgress(incrementalCheckpoint('baseline'));
  assert.equal(progress.message, 'signalr.corruptionDetect.buildingBaseline');
});

test('counting files for an incremental scan that reuses a baseline does not claim a first baseline', () => {
  const progress = formatCorruptionProgress({
    ...incrementalCheckpoint('incremental'),
    stageKey: 'signalr.corruptionDetect.enumerating'
  });
  assert.doesNotMatch(progress.detailMessage ?? '', /initialBaseline/);
});
