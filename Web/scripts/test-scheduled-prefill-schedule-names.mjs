import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';

import {
  bindLifted,
  compileToUrl,
  findSoleNode,
  liftHookCallback,
  parseSource
} from './transpile-module.mjs';

const readWebSource = (relativePath) =>
  readFileSync(new URL(`../${relativePath}`, import.meta.url), 'utf8');

const MODAL_PATH =
  'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillConfigModal.tsx';
const modalSource = parseSource(MODAL_PATH, ts.ScriptKind.TSX);

const { uniqueScheduleName } = await import(
  await compileToUrl(
    '../src/components/features/management/schedules/scheduled-prefill/scheduleNames.ts'
  )
);

const baseKey = 'management.schedules.services.scheduledPrefill.config';
const english = JSON.parse(readWebSource('src/i18n/locales/en.json'));

// Resolves the modal's own i18n lookups against the shipped en.json, so a message assertion reads
// the sentence the user sees and a missing key fails here instead of rendering as a raw key.
const translate = (key, values = {}) => {
  const template = key
    .split('.')
    .reduce((node, part) => (node === undefined ? undefined : node[part]), english);
  assert.equal(typeof template, 'string', `${key} exists in en.json`);
  return Object.entries(values).reduce(
    (text, [name, value]) => text.replaceAll(`{{${name}}}`, String(value)),
    template
  );
};

const steamName = translate(`${baseKey}.services.steam`);

// The handlers are plain consts inside the component, so they are lifted out of the TSX rather
// than imported. liftConstArrow parses as TS and chokes on the JSX around them.
const liftModalConstArrow = (constName) =>
  findSoleNode(
    modalSource,
    `${constName} declaration`,
    (node) =>
      ts.isVariableDeclaration(node) &&
      node.name.getText(modalSource) === constName &&
      node.initializer !== undefined &&
      ts.isArrowFunction(node.initializer)
  ).initializer.getText(modalSource);

let createdIds = 0;

const runScheduleHandler = (constName, config, args) => {
  let nextConfig = config;
  const handler = bindLifted(liftModalConstArrow(constName), {
    createUuid: () => `created-${(createdIds += 1)}`,
    uniqueScheduleName,
    setConfig: (updater) => {
      nextConfig = updater(nextConfig);
    },
    t: translate,
    baseKey,
    setValidationError: () => undefined,
    setSaveError: () => undefined
  });
  const createdScheduleId = handler(...args);
  return { createdScheduleId, config: nextConfig };
};

const scheduleNamesOf = (config) => config.steam.schedules.map((schedule) => schedule.name);

const withSchedules = (schedules) => ({ steam: { schedules } });

const runValidationMessage = (config) =>
  bindLifted(liftHookCallback(MODAL_PATH, 'useMemo', 'validateServiceConfig'), {
    config,
    t: translate,
    baseKey,
    SCHEDULED_PREFILL_SERVICE_RUN_ORDER: ['steam'],
    validateServiceConfig: () => null
  })();

test('a free schedule name is taken as it is and a taken one counts up', () => {
  assert.equal(uniqueScheduleName('Default copy', ['Default']), 'Default copy');
  assert.equal(uniqueScheduleName('Default copy', ['Default', 'Default copy']), 'Default copy 2');
  assert.equal(
    uniqueScheduleName('Default copy', ['Default', 'Default copy', 'Default copy 2']),
    'Default copy 3'
  );
});

test('a schedule name is taken regardless of case and surrounding spaces', () => {
  assert.equal(uniqueScheduleName('Default copy', [' default COPY ']), 'Default copy 2');
  assert.equal(
    uniqueScheduleName('Default copy', [' default COPY ', 'DEFAULT COPY 2']),
    'Default copy 3'
  );
});

test('Save as twice on one schedule produces two differently named copies', () => {
  const first = runScheduleHandler(
    'handleDuplicateSchedule',
    withSchedules([{ id: 'source', name: 'Default', enabled: true }]),
    ['steam', 'source']
  );
  const second = runScheduleHandler('handleDuplicateSchedule', first.config, ['steam', 'source']);

  const names = scheduleNamesOf(second.config);
  assert.deepEqual(names, ['Default', 'Default copy', 'Default copy 2']);
  assert.equal(
    new Set(names.map((name) => name.trim().toLowerCase())).size,
    names.length,
    'the server rejects the whole config when two names collide'
  );
});

test('Save as returns the id of the copy it created so the panel can show it', () => {
  const { createdScheduleId, config } = runScheduleHandler(
    'handleDuplicateSchedule',
    withSchedules([{ id: 'source', name: 'Default', enabled: true }]),
    ['steam', 'source']
  );

  assert.equal(config.steam.schedules[1].id, createdScheduleId);
  assert.equal(config.steam.schedules[1].name, 'Default copy');
  assert.equal(config.steam.schedules[1].enabled, true, 'a copy keeps the source On/Off state');
});

test('Add names the new schedule around the one that already holds the default name', () => {
  const { createdScheduleId, config } = runScheduleHandler(
    'handleAddSchedule',
    withSchedules([{ id: 'source', name: 'New schedule', enabled: true }]),
    ['steam']
  );

  assert.deepEqual(scheduleNamesOf(config), ['New schedule', 'New schedule 2']);
  assert.equal(config.steam.schedules[1].id, createdScheduleId);
});

test('saving is blocked while two schedules under one platform share a name', () => {
  const message = runValidationMessage(
    withSchedules([
      { id: 'a', name: 'Default', enabled: true },
      { id: 'b', name: 'default ', enabled: false }
    ])
  );

  assert.equal(
    message,
    translate(`${baseKey}.records.nameTaken`, { name: 'default', service: steamName })
  );
});

test('saving is blocked while a schedule name is blank', () => {
  const message = runValidationMessage(withSchedules([{ id: 'a', name: '   ', enabled: false }]));

  assert.equal(message, translate(`${baseKey}.records.nameRequired`));
});

test('unique non-blank schedule names raise nothing', () => {
  const message = runValidationMessage(
    withSchedules([
      { id: 'a', name: 'Default', enabled: true },
      { id: 'b', name: 'Default copy', enabled: false }
    ])
  );

  assert.equal(message, null);
});

test('Save stops before the request while validation has something to say', () => {
  const commits = [];
  const validationErrors = [];
  const handleSave = bindLifted(liftModalConstArrow('handleSave'), {
    config: withSchedules([{ id: 'a', name: 'Default', enabled: true }]),
    validationMessage: 'two schedules share a name',
    setValidationError: (message) => validationErrors.push(message),
    overwritesEnabledSchedule: () => false,
    setOverwriteEnabledConfirmOpen: () => undefined,
    commitSave: () => commits.push(true)
  });

  handleSave();

  assert.deepEqual(validationErrors, ['two schedules share a name']);
  assert.deepEqual(commits, [], 'no request goes out while a name is duplicated or blank');
});
