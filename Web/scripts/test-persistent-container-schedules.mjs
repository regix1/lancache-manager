import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';

import { bindLifted, findSoleNode, parseSource } from './transpile-module.mjs';

const SECTION_PATH = 'src/components/features/management/sections/PrefillSessionsSection.tsx';

// The two helpers ship inside a .tsx, which only exports React components, so the shipped
// code can only be driven by lifting it out of the file.
const liftArrow = (constName) => {
  const sourceFile = parseSource(SECTION_PATH, typescript.ScriptKind.TSX);
  const declaration = findSoleNode(
    sourceFile,
    `${constName} declaration`,
    (node) =>
      typescript.isVariableDeclaration(node) &&
      node.name.getText(sourceFile) === constName &&
      node.initializer !== undefined &&
      typescript.isArrowFunction(node.initializer)
  );
  return declaration.initializer.getText(sourceFile);
};

const SERVICE_RUN_ORDER = ['steam', 'epic', 'xbox', 'battleNet', 'riot'];

const persistentContainerSchedules = bindLifted(liftArrow('persistentContainerSchedules'), {
  SCHEDULED_PREFILL_SERVICE_RUN_ORDER: SERVICE_RUN_ORDER
});

// The server's stable per-platform default id (ScheduledPrefillConfig.GetDefaultScheduleId),
// written first by the v5 -> v6 migration. Kept in the fixture only: the panel reads config
// order and never has to know the value.
const STEAM_DEFAULT_SCHEDULE_ID = '5a4ec1a6-f8e6-4a60-a2c8-6b4f2478ef01';

const schedule = (id, name, overrides = {}) => ({
  id,
  name,
  enabled: true,
  intervalHours: 24,
  customSchedule: null,
  ...overrides
});

const configWith = (schedulesByKey) => ({
  version: 6,
  steam: { serviceId: 'Steam', schedules: schedulesByKey.steam ?? [] },
  epic: { serviceId: 'Epic', schedules: schedulesByKey.epic ?? [] },
  xbox: { serviceId: 'Xbox', schedules: schedulesByKey.xbox ?? [] },
  battleNet: { serviceId: 'BattleNet', schedules: schedulesByKey.battleNet ?? [] },
  riot: { serviceId: 'Riot', schedules: schedulesByKey.riot ?? [] }
});

test('lists a platform with two schedules primary-first, in config order', () => {
  const config = configWith({
    steam: [
      schedule(STEAM_DEFAULT_SCHEDULE_ID, 'Default'),
      schedule('9f1b6f5a-0000-4000-8000-000000000001', 'Nightly')
    ]
  });

  const listed = persistentContainerSchedules(config, 'Steam');

  assert.deepEqual(
    listed.map((item) => item.name),
    ['Default', 'Nightly']
  );
  assert.equal(listed[0].id, STEAM_DEFAULT_SCHEDULE_ID);
});

test('matches the container to its platform by serviceId, not by config key position', () => {
  const config = configWith({
    steam: [schedule(STEAM_DEFAULT_SCHEDULE_ID, 'Default')],
    xbox: [schedule('9f1b6f5a-0000-4000-8000-000000000003', 'Xbox default')],
    riot: [schedule('9f1b6f5a-0000-4000-8000-000000000005', 'Riot default')]
  });

  assert.deepEqual(
    persistentContainerSchedules(config, 'Xbox').map((item) => item.name),
    ['Xbox default']
  );
  assert.deepEqual(
    persistentContainerSchedules(config, 'Riot').map((item) => item.name),
    ['Riot default']
  );
});

test('lists nothing when the config is absent or the platform holds no schedules', () => {
  assert.deepEqual(persistentContainerSchedules(null, 'Steam'), []);
  assert.deepEqual(persistentContainerSchedules(configWith({}), 'Epic'), []);
});

const scheduleIntervalLabel = bindLifted(liftArrow('scheduleIntervalLabel'), {});

// Stubs i18next: returns the key so the assertions read as the wording the Schedules page uses,
// with the interpolated count appended where the real string carries one.
const translate = (key, options) => (options ? `${key}:${options.count}` : key);

const NEXT_RUN_KEY = 'management.schedules.services.scheduledPrefill.config.nextRunSummary';

test('words the interval the way the Schedules page words it', () => {
  assert.equal(
    scheduleIntervalLabel(schedule('a', 'x', { intervalHours: 0 }), translate),
    `${NEXT_RUN_KEY}.paused`
  );
  assert.equal(
    scheduleIntervalLabel(schedule('a', 'x', { intervalHours: -1 }), translate),
    `${NEXT_RUN_KEY}.startupOnly`
  );
  assert.equal(
    scheduleIntervalLabel(schedule('a', 'x', { intervalHours: 0.5 }), translate),
    'management.schedules.everyNMinutes:30'
  );
  assert.equal(
    scheduleIntervalLabel(schedule('a', 'x', { intervalHours: 6 }), translate),
    'management.schedules.everyNHours:6'
  );
});

test('a saved recurrence wins over the interval it sits beside', () => {
  const withRecurrence = schedule('a', 'x', {
    intervalHours: 0,
    customSchedule: { expression: '0 3 * * *' }
  });

  assert.equal(scheduleIntervalLabel(withRecurrence, translate), `${NEXT_RUN_KEY}.customSchedule`);
});
