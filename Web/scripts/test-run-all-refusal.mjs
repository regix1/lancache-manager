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
 * refreshes schedules, flashes the rows and clears its pending state; a real request failure raises
 * one "Failed to run all schedules" popup with the reason on its second line.
 *
 * The handler is a callback inside a component, so its branch and its message are lifted out of
 * the file that ships and run here.
 */

const SCHEDULES = 'src/components/features/management/schedules/SchedulesSection.tsx';
const runAllSource = liftHookCallback(SCHEDULES, 'useCallback', 'ApiService.runAllSchedules()');

const runAll = async ({ response, failure, recoveryFailure } = {}) => {
  const calls = [];
  const notices = [];
  const errors = [];
  const running = [];
  const confirmations = [];
  const handler = bindLifted(runAllSource, {
    sessionStore: {},
    recoverScheduledPrefillEditSession: async (_store, cleanup) => {
      calls.push('recover');
      if (recoveryFailure) throw recoveryFailure;
      await cleanup({ editSessionId: 'old-edit' });
    },
    ApiService: {
      cleanupPersistentPrefillEditSession: async () => calls.push('cleanup'),
      runAllSchedules: async () => {
        calls.push('request');
        if (failure) throw failure;
        return response;
      }
    },
    fetchSchedules: async () => calls.push('refresh'),
    flashAll: () => calls.push('flash'),
    addNotification: (notice) => notices.push(notice),
    notifyError: (message, error) => errors.push({ message, error }),
    t: (key) => key,
    setRunningAll: (value) => running.push(value),
    setRunAllConfirmOpen: (value) => confirmations.push(value)
  });

  await handler();
  return { calls, notices, errors, running, confirmations };
};

test('result fixtures without unqueued active work rely only on per-service acknowledgments', async () => {
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
    assert.deepEqual(result.calls, ['recover', 'cleanup', 'request', 'refresh', 'flash']);
    assert.deepEqual(result.notices, [], 'the HTTP fan-out adds no aggregate acknowledgment');
    assert.deepEqual(result.running, [true, false]);
    assert.deepEqual(result.confirmations, [false]);
  }
});

test('a Run All request failure raises one titled popup with the reason and finally cleanup', async () => {
  const failure = new Error('Run All transport failed');
  const result = await runAll({ failure });

  assert.deepEqual(result.calls, ['recover', 'cleanup', 'request']);
  assert.deepEqual(result.notices, []);
  assert.deepEqual(result.errors, [
    { message: 'management.schedules.runAllFailed', error: failure }
  ]);
  assert.deepEqual(result.running, [true, false]);
  assert.deepEqual(result.confirmations, [false]);
});

test('cleanup rejection prevents dispatch and still uses failure and finally handling', async () => {
  const recoveryFailure = new Error('Old edit cleanup failed');
  const result = await runAll({ recoveryFailure });

  assert.deepEqual(result.calls, ['recover']);
  assert.deepEqual(result.notices, []);
  assert.deepEqual(result.errors, [
    { message: 'management.schedules.runAllFailed', error: recoveryFailure }
  ]);
  assert.deepEqual(result.running, [true, false]);
  assert.deepEqual(result.confirmations, [false]);
});

const fetchSchedulesSource = liftHookCallback(
  SCHEDULES,
  'useCallback',
  'ApiService.getSchedules()'
);

const fetchSchedulesWith = (getSchedules, generation) => {
  const errors = [];
  const shown = [];
  const fetchSchedules = bindLifted(fetchSchedulesSource, {
    mockMode: false,
    isFetchingRef: { current: false },
    pendingRefetchRef: { current: false },
    signalrGenerationRef: generation,
    ApiService: { getSchedules },
    setSchedules: (value) => shown.push(value),
    setError: (value) => errors.push(value),
    getErrorMessage: (error) => `reason: ${error.message}`
  });
  return { fetchSchedules, errors, shown };
};

test('a failed schedules refresh is reported with its reason while rows are on screen', async () => {
  const { fetchSchedules, errors, shown } = fetchSchedulesWith(
    async () => {
      throw new Error('server down');
    },
    { current: 0 }
  );

  await fetchSchedules();

  assert.deepEqual(errors, ['reason: server down']);
  assert.deepEqual(shown, [], 'the rows already on screen stay under the box');
});

test('a schedules read that fails after a live push leaves the pushed list without an error', async () => {
  const generation = { current: 0 };
  const { fetchSchedules, errors } = fetchSchedulesWith(async () => {
    generation.current += 1;
    throw new Error('server down');
  }, generation);

  await fetchSchedules();

  assert.deepEqual(errors, []);
});

test('a failed interval change refetches the schedules and raises one titled popup', async () => {
  const failure = new Error('server refused');
  const calls = [];
  const errors = [];
  const changeInterval = bindLifted(
    liftHookCallback(SCHEDULES, 'useCallback', 'ApiService.updateSchedule(key, intervalHours)'),
    {
      schedulesRef: {
        current: [{ key: 'cacheReconciliation', customSchedule: { days: [1] }, runOnStartup: true }]
      },
      ApiService: {
        setScheduleCustomSchedule: async (key, schedule) =>
          calls.push(['clearCustom', key, schedule]),
        updateSchedule: async () => {
          calls.push(['interval']);
          throw failure;
        },
        setScheduleRunOnStartup: async () => calls.push(['runOnStartup'])
      },
      fetchSchedules: async () => calls.push(['refresh']),
      notifyError: (message, error) => errors.push({ message, error }),
      t: (key, options) => (options ? `${key}(${options.service})` : key)
    }
  );

  await changeInterval('cacheReconciliation', 6);

  // The custom schedule was already cleared on the server, so the row has to be read back.
  assert.deepEqual(calls, [
    ['clearCustom', 'cacheReconciliation', null],
    ['interval'],
    ['refresh']
  ]);
  assert.deepEqual(errors, [
    {
      message:
        'management.schedules.intervalFailed(management.schedules.services.cacheReconciliation.displayName)',
      error: failure
    }
  ]);
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

// A run this browser did not start already shows its running dot and label, so Run Now keeps its
// Play icon (disabled) and spins only for this browser's own click.
test('Run Now spins only for this browser own pending click', () => {
  const page = parseSource(SCHEDULES, typescript.ScriptKind.TSX);
  const runningElsewhere = { isRunningDot: true, isRunningOrPending: true, isPendingRun: false };
  const ownClick = { isRunningDot: false, isRunningOrPending: true, isPendingRun: true };

  const rowButton = findSoleNode(
    page,
    'schedule row Run Now content',
    (node) =>
      typescript.isConditionalExpression(node) &&
      node.whenTrue.getText(page).includes('<LoadingSpinner') &&
      node.whenFalse.getText(page).includes('schedule-run-icon')
  );
  const rowSpins = (state) =>
    Boolean(bindLifted(`() => (${rowButton.condition.getText(page)})`, state)());
  assert.equal(rowSpins(runningElsewhere), false, 'the dot and label already say Running');
  assert.equal(rowSpins(ownClick), true);

  const cardLoading = findSoleNode(
    page,
    'scheduled prefill card Run Now loading',
    (node) => typescript.isJsxAttribute(node) && node.name.getText(page) === 'runNowLoading'
  );
  const cardSpins = (state) =>
    Boolean(bindLifted(`() => (${cardLoading.initializer.expression.getText(page)})`, state)());
  assert.equal(cardSpins(runningElsewhere), false, 'the card dot already says Running');
  assert.equal(cardSpins(ownClick), true);
});
