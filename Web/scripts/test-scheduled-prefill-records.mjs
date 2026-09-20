import assert from 'node:assert/strict';
import test from 'node:test';
import ts from 'typescript';

import { bindLifted, findSoleNode, parseSource } from './transpile-module.mjs';

const SERVICE_PATH = 'src/services/api.service.ts';
const source = parseSource(SERVICE_PATH);

test('narrow writes send only their own scope and return the committed configuration', async () => {
  assert.ok(source.statements.length > 0);
  assert.equal(source.parseDiagnostics.length, 0);
  const apiService = findSoleNode(
    source,
    'ApiService',
    (node) => ts.isClassDeclaration(node) && node.name?.text === 'ApiService'
  );
  const saved = {
    version: 6,
    steam: {
      serviceId: 'Steam',
      schedules: [
        {
          id: '00000000-0000-4000-8000-000000000001',
          name: 'Evening',
          enabled: true,
          intervalHours: 24,
          customSchedule: null,
          preset: 'All',
          selectedAppIds: [],
          operatingSystems: ['Windows'],
          force: false,
          maxConcurrency: { mode: 'Auto' }
        }
      ]
    }
  };
  const schedule = saved.steam.schedules[0];
  const cases = [
    [
      'createScheduledPrefillSchedule',
      ['Steam', schedule],
      'services/Steam/schedules',
      'POST',
      schedule
    ],
    [
      'updateScheduledPrefillSchedule',
      ['Steam', schedule],
      'services/Steam/schedules/' + schedule.id,
      'PUT',
      schedule
    ],
    [
      'setScheduledPrefillScheduleEnabled',
      ['Steam', schedule.id, false],
      'services/Steam/schedules/' + schedule.id + '/enabled',
      'PUT',
      { enabled: false }
    ],
    [
      'setScheduledPrefillScheduleTiming',
      ['Steam', schedule.id, 6, null],
      'services/Steam/schedules/' + schedule.id + '/timing',
      'PUT',
      { intervalHours: 6, customSchedule: null }
    ],
    [
      'deleteScheduledPrefillSchedule',
      ['Steam', schedule.id],
      'services/Steam/schedules/' + schedule.id,
      'DELETE',
      undefined
    ],
    [
      'setScheduledPrefillSchedulesEnabled',
      [false],
      'schedules/enabled',
      'PUT',
      { enabled: false }
    ],
    [
      'setScheduledPrefillPersistence',
      ['fullPersistence'],
      'settings',
      'PUT',
      { mode: 'fullPersistence' }
    ]
  ];
  for (const [name, args, route, verb, body] of cases) {
    const method = apiService.members.find(
      (node) => ts.isMethodDeclaration(node) && node.name.getText(source) === name
    );
    assert.ok(method, name);
    const calls = [];
    const callable = bindLifted(
      '(' + method.getText(source).replace(/^static\s+async\s+\w+\(/, 'async function(') + ')',
      {
        API_BASE: '/api',
        fetch: async (...args) => {
          calls.push(args);
          return saved;
        }
      }
    );
    const result = await callable.call(
      {
        getFetchOptions: (value) => value,
        getJsonFetchOptions: (value, options) => ({ ...options, body: JSON.stringify(value) }),
        handleResponse: async (value) => value
      },
      ...args
    );
    assert.equal(result, saved);
    assert.equal(calls.length, 1);
    assert.equal(calls[0][0], '/api/system/schedules/scheduledPrefill/' + route);
    assert.equal(calls[0][1].method, verb);
    assert.deepEqual(
      calls[0][1].body === undefined ? undefined : JSON.parse(calls[0][1].body),
      body
    );
  }
});

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

test('immediate runs send the saved schedule identity to the server-owned run contract', async () => {
  const apiService = findSoleNode(
    source,
    'ApiService',
    (node) => ts.isClassDeclaration(node) && node.name?.text === 'ApiService'
  );
  const method = apiService.members.find(
    (node) =>
      ts.isMethodDeclaration(node) && node.name.getText(source) === 'runScheduledPrefillService'
  );
  assert.ok(method, 'runScheduledPrefillService');
  const calls = [];
  const callable = bindLifted(
    '(' +
      method
        .getText(source)
        .replace(/^static\s+async\s+runScheduledPrefillService\(/, 'async function(') +
      ')',
    {
      API_BASE: '/api',
      fetch: async (...args) => {
        calls.push(args);
        return { status: calls.length === 1 ? 202 : 409 };
      }
    }
  );
  const target = {
    getFetchOptions: (value) => value,
    handleResponse: async (value) => value
  };

  assert.deepEqual(await callable.call(target, 'Steam', 'schedule-a'), {
    alreadyRunning: false
  });
  assert.deepEqual(await callable.call(target, 'Steam', 'schedule-a'), {
    alreadyRunning: true
  });
  assert.equal(calls.length, 2);
  for (const [url, options] of calls) {
    assert.equal(
      url,
      '/api/system/schedules/scheduledPrefill/services/Steam/schedules/schedule-a/run'
    );
    assert.deepEqual(options, { method: 'POST' });
  }
});
