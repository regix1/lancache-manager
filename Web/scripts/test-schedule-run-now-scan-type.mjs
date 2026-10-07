import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import {
  bindLifted,
  compileToUrl,
  liftConstArrow,
  loadNotificationModules,
  moduleUrl,
  operationRunRow,
  pushRun
} from './transpile-module.mjs';

/**
 * Game Detection's Run Now is "already running" only for a detection of the scan type the schedule
 * would run. A detection of the other type does the other scan's work, so a click waits behind it
 * and has to stay possible. The type comes from the scan mode the run rows carry, never from a name.
 */

const SECTION = 'src/components/features/management/schedules/SchedulesSection.tsx';
const hasScheduleScanRun = bindLifted(liftConstArrow(SECTION, 'hasScheduleScanRun'), {});

const detection = (status, scanType) => ({ type: 'game_detection', status, details: { scanType } });

test('a running or waiting detection of the same scan type is the schedule own run', () => {
  assert.equal(hasScheduleScanRun([detection('running', 'full')], 'full'), true);
  assert.equal(hasScheduleScanRun([detection('waiting', 'incremental')], 'incremental'), true);
});

test('a detection of the other scan type leaves Run Now clickable', () => {
  assert.equal(hasScheduleScanRun([detection('running', 'incremental')], 'full'), false);
  assert.equal(hasScheduleScanRun([detection('waiting', 'full')], 'incremental'), false);
});

test('an ended detection and a run of another type do not count', () => {
  assert.equal(hasScheduleScanRun([detection('completed', 'full')], 'full'), false);
  assert.equal(
    hasScheduleScanRun([{ type: 'corruption_detection', status: 'running', details: {} }], 'full'),
    false
  );
});

test('a hybrid schedule blocks Run Now only for the type its next run would request', () => {
  // The server resolves hybrid to one scan type per run and sends it as runNowScanType.
  assert.equal(hasScheduleScanRun([detection('running', 'incremental')], 'incremental'), true);
  assert.equal(hasScheduleScanRun([detection('running', 'full')], 'incremental'), false);
  assert.equal(hasScheduleScanRun([detection('waiting', 'incremental')], 'full'), false);
});

test('a schedule that names no scan type claims no run', () => {
  assert.equal(hasScheduleScanRun([detection('running', 'full')], null), false);
});

test('the schedule list keeps only a scan type the browser knows', async () => {
  const { readSchedules } = await import(
    await compileToUrl('../src/components/features/management/schedules/types.ts')
  );
  const rows = readSchedules([
    { key: 'gameDetection', runNowScanType: 'incremental' },
    { key: 'gameDetectionFull', runNowScanType: 'full' },
    { key: 'unknownType', runNowScanType: 'hybrid' },
    { key: 'missing' },
    { key: 'other', runNowScanType: null }
  ]);
  assert.deepEqual(
    rows.map((row) => row.runNowScanType),
    ['incremental', 'full', null, null, null]
  );
});

test('both places the list is read go through the same check', () => {
  const source = readFileSync(new URL(`../${SECTION}`, import.meta.url), 'utf8');
  assert.equal(source.match(/setSchedules\(readSchedules\(data\)\)/g)?.length, 2);
  assert.match(source, /hasScheduleScanRun\(runs, service\.runNowScanType \?\? null\)/);
});

test('the row gates Run Now on its own scan run, not on the dot', () => {
  const source = readFileSync(new URL(`../${SECTION}`, import.meta.url), 'utf8');
  assert.match(
    source,
    /const isRunningOrPending = \(scanMode !== null \? ownScanRunActive : isRunningDot\) \|\| isPendingRun;/
  );
});

test('a detection run row carries its scan type to the card, and no other run does', async () => {
  const modules = await loadNotificationModules(
    moduleUrl('export default { t: (key) => key, exists: () => false };')
  );
  const cards = (rows) => {
    let state = modules.createRunStoreState();
    for (const row of rows) state = pushRun(modules, state, row);
    return modules.deriveRuns(state);
  };

  const [full, quick] = cards([
    operationRunRow('D1', {
      operationType: 'gameDetection',
      name: 'Game Detection',
      scanMode: 'full'
    }),
    operationRunRow('D2', {
      operationType: 'gameDetection',
      name: 'Game Detection',
      scanMode: 'incremental',
      status: 'waiting'
    })
  ]).sort((a, b) => a.id.localeCompare(b.id));
  assert.equal(full.details.scanType, 'full');
  assert.equal(quick.details.scanType, 'incremental');

  const [corruption] = cards([
    operationRunRow('C1', {
      operationType: 'corruptionDetection',
      name: 'Corruption Detection',
      scanMode: 'full'
    })
  ]);
  assert.equal(corruption.details.scanType, undefined);
});
