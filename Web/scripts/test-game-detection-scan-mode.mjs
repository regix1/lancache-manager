import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

import { compileToUrl } from './transpile-module.mjs';

const types = await import(
  await compileToUrl('../src/components/features/management/schedules/types.ts')
);
const { isGameDetectionScanMode } = types;

const sectionSource = readFileSync(
  new URL('../src/components/features/management/schedules/SchedulesSection.tsx', import.meta.url),
  'utf8'
);

test('the guard accepts exactly the three modes the dropdown offers', () => {
  assert.equal(isGameDetectionScanMode('full'), true);
  assert.equal(isGameDetectionScanMode('incremental'), true);
  assert.equal(isGameDetectionScanMode('hybrid'), true);
});

// github is the fourth depot-crawl mode and means "PICS updates only", which detection cannot do.
// Letting it through the guard would PUT a mode the server refuses and leave the dropdown showing a
// selection that never saved.
test('the guard rejects the depot-only mode and anything unrecognized', () => {
  assert.equal(isGameDetectionScanMode('github'), false);
  assert.equal(isGameDetectionScanMode('Full'), false);
  assert.equal(isGameDetectionScanMode(''), false);
});

test('the dropdown offers full first, then incremental, then hybrid', () => {
  const dropdown = sectionSource.slice(
    sectionSource.indexOf('const GameDetectionModeDropdown'),
    sectionSource.indexOf('interface ScheduleRowProps')
  );
  assert.ok(dropdown.length > 0, 'GameDetectionModeDropdown not found in SchedulesSection.tsx');

  const values = [...dropdown.matchAll(/value: '([a-z]+)'/g)].map((match) => match[1]);
  assert.deepEqual(values, ['full', 'incremental', 'hybrid']);
});

// Only the schedule that has a scan mode is sent one, so the card reads the value's presence rather
// than comparing the service key. A key comparison here could disagree with the server about which
// card owns the setting.
test('the scan-mode row and chip are gated on the value being present, not on the service key', () => {
  assert.ok(sectionSource.includes('const scanMode = service.scanMode ?? null;'));
  assert.equal(sectionSource.split('{scanMode !== null && (').length - 1, 2);
  assert.ok(!sectionSource.includes("service.key === 'gameDetection'"));
});

test('choosing a mode saves it through the schedule scan-mode route', () => {
  assert.ok(sectionSource.includes('await ApiService.setScheduleScanMode(key, mode);'));
  // Optimistic like every other control in this disclosure, so the dropdown flips before the PUT
  // resolves and a failure refetches the authoritative value rather than leaving the wrong one shown.
  assert.ok(
    sectionSource.includes('prev.map((s) => (s.key === key ? { ...s, scanMode: mode } : s))')
  );
});

test('the save route matches the controller route', () => {
  const apiSource = readFileSync(
    new URL('../src/services/api.service.ts', import.meta.url),
    'utf8'
  );
  assert.ok(apiSource.includes('`${API_BASE}/system/schedules/${serviceKey}/scanMode`'));
});
