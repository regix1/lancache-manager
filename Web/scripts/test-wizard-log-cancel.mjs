import assert from 'node:assert/strict';
import test from 'node:test';
import ts from 'typescript';
import { findSoleNode, parseSource } from './transpile-module.mjs';

/**
 * Cancel on the setup wizard's log step force stops the pass. A pass that saved its success just
 * before the stop still completes, so the "cancelled" notice has to come from the completion event
 * (its `cancelled` flag), never from the click, and a success must clear any notice.
 */

const stepFile = 'src/components/initialization/steps/LogProcessingStep.tsx';
const source = parseSource(stepFile, ts.ScriptKind.TSX);

const handlerText = (name) =>
  findSoleNode(
    source,
    `${name} declaration`,
    (node) => ts.isVariableDeclaration(node) && node.name.getText(source) === name
  ).getText(source);

test('the cancel click does not write the cancelled notice', () => {
  assert.ok(
    !handlerText('handleCancelProcessing').includes('initialization.logProcessing.cancelled'),
    'the notice comes from the completion event, not from the click'
  );
});

test('the cancel click leaves the step running until the pass reports its ending', () => {
  assert.ok(
    !handlerText('handleCancelProcessing').includes('setProcessing(false)'),
    'the completion event or the watchdog ends the running view, not the click'
  );
});

test("the watchdog reads the pass's own ending", () => {
  const watchdogEffect = findSoleNode(
    source,
    'watchdog effect',
    (node) =>
      ts.isCallExpression(node) &&
      node.getText(source).startsWith('useEffect(') &&
      node.getText(source).includes('const watchdog = setInterval')
  ).getText(source);
  assert.ok(watchdogEffect.includes('getTrackedOperation'), 'the run says how the pass ended');
  assert.ok(
    watchdogEffect.includes('initialization.logProcessing.cancelled'),
    'a cancelled run shows the cancelled notice'
  );
});

test('a successful completion clears the notice', () => {
  const completion = handlerText('handleLogProcessingComplete');
  const successBranch = completion.slice(completion.indexOf('// Success case'));
  assert.ok(successBranch.includes('setNotice(null)'), 'a finished pass shows no notice');
});
