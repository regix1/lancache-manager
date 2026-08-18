import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import { clockFromTimeSetting, timeSettingFromClock } from '../src/utils/pendingPreferences.ts';
import { transpile } from './transpile-module.mjs';

/**
 * A guest connects and the administrator's guest defaults answer before the guest's own preferences
 * do. The clock on screen in that moment is the one the picker starts with, not the one the guest
 * stored. The effect that moves a guest off a clock the administrator does not allow saves what it
 * decides, so deciding on the starting clock overwrites the guest's pick and the guard it sets stops
 * it correcting itself.
 *
 * This repo has no component renderer, so the effect is taken out of the file's syntax tree and run
 * against the values the component holds at that moment.
 */

const SELECTOR_PATH = 'src/components/common/TimezoneSelector.tsx';

const sourceFile = ts.createSourceFile(
  'TimezoneSelector.tsx',
  readFileSync(new URL(`../${SELECTOR_PATH}`, import.meta.url), 'utf8'),
  ts.ScriptTarget.Latest,
  true,
  ts.ScriptKind.TSX
);

/** The effect that moves a guest to an allowed clock, as a callable. */
const readAutoSwitchEffect = () => {
  const calls = [];
  const visit = (node) => {
    if (
      ts.isCallExpression(node) &&
      node.expression.getText(sourceFile) === 'useEffect' &&
      node.arguments.length === 2 &&
      node.arguments[0].getText(sourceFile).includes('hasAutoSwitched')
    ) {
      calls.push(node);
    }
    ts.forEachChild(node, visit);
  };
  ts.forEachChild(sourceFile, visit);

  assert.equal(calls.length, 1, `expected exactly one auto-switch effect in ${SELECTOR_PATH}`);

  // The compiler puts its own prologue in front of the expression, which a `return` would stop at.
  const body = transpile(`(${calls[0].arguments[0].getText(sourceFile)})`)
    .replace(/^"use strict";\s*/, '')
    .trim()
    .replace(/;$/, '');
  return new Function(
    'isGuest',
    'loadingDefaults',
    'currentPreferences',
    'hasAutoSwitched',
    'getCurrentValue',
    'getEffectiveAllowedFormats',
    'getAdminDefault',
    'timeSettingFromClock',
    'handleTimeSettingChange',
    `return (${body})();`
  );
};

/**
 * Runs the effect for one guest at the moment the administrator's defaults have landed.
 *
 * @param {{ stored: string | null, onScreen: string, allowed: string[], adminDefault: string }} arrival
 *   `stored` is the guest's own clock, or null while it is still on the wire; `onScreen` is the clock
 *   the picker reads until then.
 * @returns {string[]} The clocks the effect saved.
 */
const runOnArrival = ({ stored, onScreen, allowed, adminDefault }) => {
  const saved = [];
  readAutoSwitchEffect()(
    true,
    false,
    stored === null ? null : clockFromTimeSetting(stored),
    { current: false },
    () => onScreen,
    () => allowed,
    () => adminDefault,
    timeSettingFromClock,
    (value) => saved.push(value)
  );
  return saved;
};

test('a guest whose stored clock is allowed keeps it when the starting clock is not', () => {
  const saved = runOnArrival({
    stored: 'utc',
    onScreen: 'server-24h',
    allowed: ['utc', 'local-12h'],
    adminDefault: 'local-12h'
  });

  assert.deepEqual(
    saved,
    [],
    'the guest kept a clock the administrator allows, so nothing is saved'
  );
});

test('nothing is saved before the guest own preferences arrive', () => {
  const saved = runOnArrival({
    stored: null,
    onScreen: 'server-24h',
    allowed: ['utc'],
    adminDefault: 'utc'
  });

  assert.deepEqual(saved, [], 'the effect decided on a clock that was never the guest own');
});

test('a guest whose stored clock is not allowed is moved to the administrator default', () => {
  const saved = runOnArrival({
    stored: 'server-12h',
    onScreen: 'server-12h',
    allowed: ['utc', 'local-24h'],
    adminDefault: 'local-24h'
  });

  assert.deepEqual(saved, ['local-24h']);
});
