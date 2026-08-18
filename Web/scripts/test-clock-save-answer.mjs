import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import { transpile } from './transpile-module.mjs';

/**
 * What the picker does with each of the three answers a clock save can come back with. A save the
 * server took releases the pick; one it refused takes the pick back and says so; one it never
 * answered takes the pick back too, but the client does not know the save went nowhere and must
 * not say it did, because the request may be committing and the broadcast is about to move the
 * clock.
 *
 * This repo has no component renderer, so the handler is taken out of the file's syntax tree and
 * run against stubs for everything it reaches for.
 */

const SELECTOR_PATH = 'src/components/common/TimezoneSelector.tsx';

const sourceFile = ts.createSourceFile(
  'TimezoneSelector.tsx',
  readFileSync(new URL(`../${SELECTOR_PATH}`, import.meta.url), 'utf8'),
  ts.ScriptTarget.Latest,
  true,
  ts.ScriptKind.TSX
);

/** The handler the picker runs on a pick, as a callable over its free names. */
const readSaveHandler = () => {
  const declarations = [];
  const visit = (node) => {
    if (
      ts.isVariableDeclaration(node) &&
      ts.isIdentifier(node.name) &&
      node.name.text === 'handleTimeSettingChange'
    ) {
      declarations.push(node);
    }
    ts.forEachChild(node, visit);
  };
  ts.forEachChild(sourceFile, visit);

  assert.equal(
    declarations.length,
    1,
    `expected exactly one handleTimeSettingChange in ${SELECTOR_PATH}`
  );

  // The compiler puts its own prologue in front of the expression, which a `return` would stop at.
  const body = transpile(`(${declarations[0].initializer.getText(sourceFile)})`).replace(
    /^"use strict";\s*/,
    ''
  );
  return new Function(
    't',
    'preferencesService',
    'setPendingTimeSetting',
    'dropPendingTimeSetting',
    'confirmPendingTimezone',
    'clockFromTimeSetting',
    'notifyError',
    `return ${body}`
  );
};

/** The number the store hands back for the pick these runs make. */
const CLICK = 7;

const runSave = async (answer) => {
  const confirmed = [];
  const dropped = [];
  const errors = [];

  const handle = readSaveHandler()(
    (key) => key,
    { setClockPreferences: async () => answer },
    () => CLICK,
    (click) => dropped.push(click),
    (click) => confirmed.push(click),
    (value) => value,
    (message) => errors.push(message)
  );
  await handle('utc');

  return { confirmed, dropped, errors };
};

test('a save the server took releases the pick and says nothing', async () => {
  const { confirmed, dropped, errors } = await runSave('saved');

  assert.deepEqual(
    confirmed,
    [CLICK],
    'the yes is the only thing that tells the store the server is holding the pick'
  );
  assert.deepEqual(dropped, [], 'a pick the server took must not be taken back');
  assert.deepEqual(errors, []);
});

test('a save the server refused takes the pick back and says so', async () => {
  const { confirmed, dropped, errors } = await runSave('failed');

  assert.deepEqual(dropped, [CLICK]);
  assert.deepEqual(confirmed, []);
  assert.deepEqual(errors, ['common.timezoneSelector.errors.updateFailed']);
});

test('a save nothing answered does not tell the reader it failed', async () => {
  const { confirmed, dropped, errors } = await runSave('noAnswer');

  assert.deepEqual(dropped, [CLICK], 'the pick cannot be held on an answer that never came');
  assert.deepEqual(confirmed, []);
  assert.deepEqual(
    errors,
    ['common.timezoneSelector.errors.updateNoAnswer'],
    'a request that ran out of time is not a save the server turned down'
  );
});
