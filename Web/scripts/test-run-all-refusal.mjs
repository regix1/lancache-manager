import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import { bindLifted, collectNodes, findSoleNode, parseSource } from './transpile-module.mjs';

/**
 * Run All fans a trigger out across every schedule and gets back three counts: what started, what
 * was already running and now has a second run armed behind it, and what the download gate refused.
 * The refusal used to be announced through the success path, so a fan-out that started nothing at
 * all reported itself in green, and the two counts shared one message that had room for only one
 * of them, so the services that will run again went unmentioned whenever anything was refused.
 *
 * The handler is a callback inside a component, so its branch and its message are lifted out of
 * the file that ships and run here.
 */

const SCHEDULES = 'src/components/features/management/schedules/SchedulesSection.tsx';
const REASON = 'A client download is writing to the cache right now. Try again once it finishes.';

const sourceFile = parseSource(SCHEDULES, typescript.ScriptKind.TSX);

/** The `if (refused > 0)` statement Run All answers a refusal through. */
const refusedBranch = findSoleNode(
  sourceFile,
  'the Run All refusal branch',
  (node) => typescript.isIfStatement(node) && node.expression.getText(sourceFile) === 'refused > 0'
);

test('a fan-out that refused a service does not report success', () => {
  const taken = refusedBranch.thenStatement.getText(sourceFile);

  assert.match(taken, /status: 'skipped'/, 'a refusal is reported as a refusal');
  assert.match(taken, /notificationType: 'warning'/);
  assert.doesNotMatch(
    taken,
    /notifySuccess/,
    'nothing started is not a success, whatever the started count says'
  );
  assert.match(
    refusedBranch.elseStatement.getText(sourceFile),
    /notifySuccess/,
    'a clean fan-out still reports success'
  );
});

/** The message that branch builds, with `t` handing back the key and its values. */
const refusalMessage = (values) => {
  const conditional = findSoleNode(
    refusedBranch.thenStatement,
    'the refusal message',
    (node) =>
      typescript.isConditionalExpression(node) &&
      node.condition.getText(sourceFile) === 'queuedNext > 0'
  );
  return bindLifted(`() => (${conditional.getText(sourceFile)})`, {
    ...values,
    t: (key, args) => ({ key, args })
  })();
};

test('the services that will run again survive a refusal in the same fan-out', () => {
  const both = refusalMessage({
    triggeredCount: 1,
    queuedNext: 2,
    refused: 3,
    skippedReason: REASON
  });

  assert.equal(both.key, 'management.schedules.runAllTriggeredWithSkippedAndQueued');
  assert.deepEqual(both.args, { count: 1, queued: 2, skipped: 3, reason: REASON });
});

test('a refusal with nothing already running keeps the plainer sentence', () => {
  const only = refusalMessage({
    triggeredCount: 0,
    queuedNext: 0,
    refused: 3,
    skippedReason: REASON
  });

  assert.equal(only.key, 'management.schedules.runAllTriggeredWithSkipped');
  assert.deepEqual(only.args, { count: 0, skipped: 3, reason: REASON });
});

/**
 * Four Management sections read the scan gate, so one mount and every announcement asked the
 * server the same question four times. The service shares the in-flight promise, the way it
 * already does for the cached detection both StorageSection and GameCacheDetector load.
 */
test('the four readers of the scan gate share one request', () => {
  const service = parseSource('src/services/api.service.ts');
  const method = findSoleNode(
    service,
    'getCacheScanBlocked',
    (node) =>
      typescript.isMethodDeclaration(node) && node.name.getText(service) === 'getCacheScanBlocked'
  );

  assert.equal(
    method.parameters.length,
    0,
    'a shared promise cannot honour one caller’s AbortSignal without cancelling the others'
  );

  const body = method.body.getText(service);
  assert.match(
    body,
    /if \(ApiService\._cacheScanBlockedInFlight\) \{\s*return ApiService\._cacheScanBlockedInFlight;/,
    'a second caller in the same tick joins the request already going out'
  );

  const cleared = collectNodes(
    method,
    (node) =>
      typescript.isBlock(node) &&
      node.getText(service).includes('ApiService._cacheScanBlockedInFlight = null')
  );
  assert.ok(cleared.length > 0, 'the shared promise has to be released once it settles');
  assert.ok(
    method.body.getText(service).includes('} finally {'),
    'released in finally, so a failed read does not pin a rejected promise for every later caller'
  );
});
