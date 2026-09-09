import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import {
  bindLifted,
  collectNodes,
  findSoleNode,
  liftHookCallback,
  parseSource
} from './transpile-module.mjs';

/**
 * Run All fans a trigger out across every schedule. Each service's own lifecycle or retained-run
 * notice is the acknowledgment owner, so the HTTP response never adds an aggregate success,
 * queued, held or skipped toast beside those notices. The button still waits for the request,
 * refreshes schedules, flashes the rows and clears its pending state; a real request failure still
 * uses the existing error notification.
 *
 * The handler is a callback inside a component, so its branch and its message are lifted out of
 * the file that ships and run here.
 */

const SCHEDULES = 'src/components/features/management/schedules/SchedulesSection.tsx';
const runAllSource = liftHookCallback(SCHEDULES, 'useCallback', 'ApiService.runAllSchedules()');

const runAll = async ({ response, failure } = {}) => {
  const calls = [];
  const notices = [];
  const running = [];
  const confirmations = [];
  const handler = bindLifted(runAllSource, {
    ApiService: {
      runAllSchedules: async () => {
        calls.push('request');
        if (failure) throw failure;
        return response;
      }
    },
    fetchSchedules: async () => calls.push('refresh'),
    flashAll: () => calls.push('flash'),
    addNotification: (notice) => notices.push(notice),
    getErrorMessage: (error) => error.message,
    t: (key) => key,
    setRunningAll: (value) => running.push(value),
    setRunAllConfirmOpen: (value) => confirmations.push(value)
  });

  await handler();
  return { calls, notices, running, confirmations };
};

test('normal, queued and refused fan-outs rely only on per-service acknowledgments', async () => {
  for (const response of [
    { triggeredCount: 4, alreadyRunningCount: 0, skippedCount: 0 },
    { triggeredCount: 2, alreadyRunningCount: 2, skippedCount: 0 },
    {
      triggeredCount: 1,
      alreadyRunningCount: 2,
      skippedCount: 3,
      skippedReason: 'management.schedules.queuedUntilCacheFree'
    }
  ]) {
    const result = await runAll({ response });
    assert.deepEqual(result.calls, ['request', 'refresh', 'flash']);
    assert.deepEqual(result.notices, [], 'the HTTP fan-out adds no aggregate acknowledgment');
    assert.deepEqual(result.running, [true, false]);
    assert.deepEqual(result.confirmations, [false]);
  }
});

test('a Run All request failure still uses the existing error notification and finally cleanup', async () => {
  const result = await runAll({ failure: new Error('Run All transport failed') });

  assert.deepEqual(result.calls, ['request']);
  assert.deepEqual(result.notices, [
    {
      type: 'generic',
      status: 'failed',
      message: 'Run All transport failed',
      details: { notificationType: 'error' }
    }
  ]);
  assert.deepEqual(result.running, [true, false]);
  assert.deepEqual(result.confirmations, [false]);
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
