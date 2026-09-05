import assert from 'node:assert/strict';
import test from 'node:test';
import ts from 'typescript';

import { bindLifted, findSoleNode, parseSource } from './transpile-module.mjs';

const SERVICE_PATH = 'src/services/api.service.ts';
const source = parseSource(SERVICE_PATH);

function liftFunction(name, bindings = {}) {
  const declaration = findSoleNode(
    source,
    `${name} declaration`,
    (node) => ts.isFunctionDeclaration(node) && node.name?.text === name
  );
  return bindLifted(declaration.getText(source), bindings);
}

function liftUpdateMethod(bindings) {
  const apiService = findSoleNode(
    source,
    'ApiService declaration',
    (node) => ts.isClassDeclaration(node) && node.name?.text === 'ApiService'
  );
  const method = apiService.members.find(
    (candidate) =>
      ts.isMethodDeclaration(candidate) &&
      candidate.name.getText(source) === 'updateScheduledPrefillConfig'
  );
  assert.ok(method, 'updateScheduledPrefillConfig exists');
  const callable = method
    .getText(source)
    .replace(
      /^static\s+async\s+updateScheduledPrefillConfig\(/,
      'async function updateScheduledPrefillConfig('
    );
  return bindLifted(`(${callable})`, bindings);
}

const getLegacyScheduleId = liftFunction('getLegacyScheduleId');
const normalizeLegacyServiceConfig = liftFunction('normalizeLegacyServiceConfig', {
  getLegacyScheduleId
});
const normalizeScheduledPrefillConfig = liftFunction('normalizeScheduledPrefillConfig', {
  NAMED_SCHEDULES_VERSION: 6,
  normalizeLegacyServiceConfig
});
const assertNamedSchedulesVersion = liftFunction('assertNamedSchedulesVersion', {
  NAMED_SCHEDULES_VERSION: 6
});
const normalizeScheduledPrefillSummary = liftFunction('normalizeScheduledPrefillSummary', {
  getLegacyScheduleId
});

const persistentTypes = parseSource('src/components/features/prefill/persistentPrefillTypes.ts');
const getPersistentPrefillRunOptions = bindLifted(
  findSoleNode(
    persistentTypes,
    'getPersistentPrefillRunOptions declaration',
    (node) => ts.isFunctionDeclaration(node) && node.name?.text === 'getPersistentPrefillRunOptions'
  )
    .getText(persistentTypes)
    .replace(/^export\s+/, ''),
  {}
);
const legacyService = (serviceId, selectedAppIds = []) => ({
  serviceId,
  enabled: true,
  intervalHours: 24,
  customSchedule: null,
  preset: 'All',
  selectedAppIds,
  topCount: null,
  operatingSystems: ['Windows'],
  force: false,
  maxConcurrency: { mode: 'Auto', value: null }
});

const legacyConfig = () => ({
  version: 5,
  maxServiceRuntime: '01:00:00',
  stallTimeout: '00:15:00',
  persistenceMode: 'killOnRestart',
  steam: legacyService('Steam', null),
  epic: legacyService('Epic', ['1']),
  xbox: legacyService('Xbox'),
  battleNet: legacyService('BattleNet'),
  riot: legacyService('Riot')
});

test('v5 scheduled prefill config becomes stable read-only child records', () => {
  const normalized = normalizeScheduledPrefillConfig(legacyConfig());

  assert.equal(normalized.version, 5);
  assert.deepEqual(normalized.steam.schedules, [
    {
      enabled: true,
      intervalHours: 24,
      customSchedule: null,
      preset: 'All',
      selectedAppIds: [],
      topCount: null,
      operatingSystems: ['Windows'],
      force: false,
      maxConcurrency: { mode: 'Auto', value: null },
      id: 'legacy-steam',
      name: 'Existing schedule'
    }
  ]);
  assert.equal(normalized.epic.schedules[0].id, 'legacy-epic');
  assert.deepEqual(normalized.epic.schedules[0].selectedAppIds, ['1']);
});

test('an incomplete v6 service reports the newer-server error', () => {
  const incomplete = {
    ...legacyConfig(),
    version: 6
  };

  assert.throws(
    () => normalizeScheduledPrefillConfig(incomplete),
    /Named schedules require a newer server version\./
  );
});

test('v5 schedule summaries receive the same stable child identity', () => {
  const summary = normalizeScheduledPrefillSummary({
    serviceId: 'Steam',
    intervalHours: 24,
    enabled: true,
    isRunning: false,
    operationId: null,
    lastRunUtc: null,
    nextRunUtc: null
  });

  assert.equal(summary.scheduleId, 'legacy-steam');
  assert.equal(summary.name, 'Existing schedule');
});

test('v5 records cannot issue a named-record update request', async () => {
  const updateScheduledPrefillConfig = liftUpdateMethod({ assertNamedSchedulesVersion });

  await assert.rejects(
    updateScheduledPrefillConfig.call({}, normalizeScheduledPrefillConfig(legacyConfig())),
    /newer server version/
  );
});

test('opened records keep selected, all, recent, and top download semantics for immediate runs', () => {
  const baseSchedule = {
    id: 'schedule-a',
    name: 'Schedule A',
    enabled: false,
    intervalHours: 24,
    customSchedule: null,
    notificationMode: 'all',
    notificationDisplayMode: 'full',
    selectedAppIds: [],
    topCount: null,
    operatingSystems: ['Windows', 'Linux'],
    force: true,
    maxConcurrency: { mode: 'Fixed', value: 4 }
  };

  assert.deepEqual(getPersistentPrefillRunOptions({ ...baseSchedule, preset: 'All' }), {
    appIds: [],
    all: true,
    recent: false,
    recentlyPurchased: false,
    top: null,
    force: true,
    operatingSystems: ['windows', 'linux'],
    maxConcurrency: 4
  });
  assert.deepEqual(getPersistentPrefillRunOptions({ ...baseSchedule, preset: 'Recent' }), {
    appIds: [],
    all: false,
    recent: true,
    recentlyPurchased: false,
    top: null,
    force: true,
    operatingSystems: ['windows', 'linux'],
    maxConcurrency: 4
  });
  assert.deepEqual(
    getPersistentPrefillRunOptions({ ...baseSchedule, preset: 'Top', topCount: 12 }),
    {
      appIds: [],
      all: false,
      recent: false,
      recentlyPurchased: false,
      top: 12,
      force: true,
      operatingSystems: ['windows', 'linux'],
      maxConcurrency: 4
    }
  );
  assert.deepEqual(
    getPersistentPrefillRunOptions({ ...baseSchedule, preset: 'All', selectedAppIds: ['20'] }),
    {
      appIds: ['20'],
      all: false,
      recent: false,
      recentlyPurchased: false,
      top: null,
      force: true,
      operatingSystems: ['windows', 'linux'],
      maxConcurrency: 4
    }
  );
});
