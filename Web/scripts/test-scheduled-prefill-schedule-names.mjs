import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';

import { bindLifted, compileToUrl, findSoleNode, parseSource } from './transpile-module.mjs';

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

const detailSource = parseSource(
  'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillScheduleDetail.tsx',
  ts.ScriptKind.TSX
);
const liftConstArrow = (source, name, bindings) => {
  assert.ok(source.statements.length);
  assert.equal(source.parseDiagnostics.length, 0);
  const declaration = findSoleNode(
    source,
    name,
    (node) => ts.isVariableDeclaration(node) && node.name.getText(source) === name
  );
  return bindLifted(declaration.initializer.getText(source), bindings);
};
const schedule = (patch = {}) => ({
  id: '00000000-0000-4000-8000-000000000001',
  name: 'Evening',
  enabled: true,
  intervalHours: 24,
  customSchedule: null,
  preset: 'All',
  selectedAppIds: ['10'],
  topCount: null,
  operatingSystems: ['Windows'],
  force: false,
  maxConcurrency: { mode: 'Auto' },
  notificationMode: 'all',
  notificationDisplayMode: 'full',
  ...patch
});
test('a schedule name is unique regardless of case and surrounding spaces', () => {
  assert.equal(uniqueScheduleName('Evening', [' EVENING ']), 'Evening 2');
  assert.equal(uniqueScheduleName('Evening', ['Evening', 'Evening 2']), 'Evening 3');
  assert.equal(uniqueScheduleName('Evening', []), 'Evening');
});
for (const copy of [false, true]) {
  test(
    copy
      ? 'Duplicate preserves enablement and uses a unique name without writing'
      : 'Add copies the first record, starts disabled and creates nothing before Save',
    async () => {
      let target;
      const source = schedule();
      const records = [
        source,
        schedule({
          id: '00000000-0000-4000-8000-000000000002',
          name: copy ? 'Evening copy' : 'New schedule'
        })
      ];
      await liftConstArrow(detailSource, 'createDraft', {
        opening: { current: 0 },
        ApiService: { getScheduledPrefillConfig: async () => ({ steam: { schedules: records } }) },
        createUuid: () => '00000000-0000-4000-8000-000000000003',
        t: translate,
        baseKey,
        uniqueScheduleName,
        setModalRecord: (value) => {
          target = value;
        },
        setError: assert.fail,
        getErrorMessage: (error) => error.message
      })('steam', copy ? source.id : undefined);
      assert.equal(target.create, true);
      assert.equal(target.schedule.enabled, copy);
      assert.equal(target.schedule.name, copy ? 'Evening copy 2' : 'New schedule 2');
      assert.deepEqual(target.schedule.selectedAppIds, ['10']);
      assert.notEqual(target.schedule.id, source.id);
      assert.equal(records.length, 2);
    }
  );
}
test('an empty service Add draft uses documented defaults', async () => {
  let target;
  await liftConstArrow(detailSource, 'createDraft', {
    opening: { current: 0 },
    ApiService: { getScheduledPrefillConfig: async () => ({ riot: { schedules: [] } }) },
    createUuid: () => '00000000-0000-4000-8000-000000000003',
    t: translate,
    baseKey,
    uniqueScheduleName,
    setModalRecord: (value) => {
      target = value;
    },
    setError: assert.fail,
    getErrorMessage: (error) => error.message
  })('riot');
  assert.deepEqual(
    target.schedule,
    schedule({
      id: '00000000-0000-4000-8000-000000000003',
      name: 'New schedule',
      enabled: false,
      selectedAppIds: [],
      operatingSystems: []
    })
  );
});
test('blank names stop Save before the request', () => {
  let message;
  liftConstArrow(modalSource, 'handleSave', {
    config: schedule({ name: ' ' }),
    target: { serviceKey: 'steam' },
    t: translate,
    baseKey,
    validateServiceConfig: () => null,
    setError: (value) => {
      message = value;
    },
    setOverwriteEnabledConfirmOpen: assert.fail,
    commitSave: assert.fail
  })();
  assert.equal(message, translate(baseKey + '.records.nameRequired'));
});
