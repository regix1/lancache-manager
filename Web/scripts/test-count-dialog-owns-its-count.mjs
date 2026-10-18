import assert from 'node:assert/strict';
import test from 'node:test';
import { bindLifted, liftHookCallback } from './transpile-module.mjs';

/**
 * The removal dialogs start a server count when they open and cancel it when they close. The
 * server gives every start its own count, so a dialog cancels only the count it started: a reopened
 * dialog, a Retry or another tab never waits on that cancel, and never sees it end their count.
 *
 * The count effect is lifted out of the component as shipped and run against a server model whose
 * first start answers only when the test says so.
 */

const effectSource = liftHookCallback(
  'src/components/features/management/game-detection/GameCacheDetector.tsx',
  'useEffect',
  'Could not start the cache file count'
);

const flush = () => new Promise((resolve) => setTimeout(resolve, 20));
const waitForRetry = () => new Promise((resolve) => setTimeout(resolve, 150));

const createServer = () => {
  const statuses = new Map();
  let answerFirstStart = null;
  return {
    statuses,
    answerFirstStart: () => answerFirstStart(),
    api: {
      startUnmappedCacheFileCount() {
        const operationId = `count-${statuses.size + 1}`;
        statuses.set(operationId, 'Waiting');
        if (statuses.size > 1) return Promise.resolve({ operationId });
        return new Promise((resolve) => {
          answerFirstStart = () => resolve({ operationId });
        });
      },
      startServiceCacheFileCount() {
        return this.startUnmappedCacheFileCount();
      },
      cancelOperation(operationId) {
        statuses.set(operationId, 'Cancelled');
        return Promise.resolve();
      },
      getCacheFileCountStatus(operationId) {
        return Promise.resolve(
          statuses.get(operationId) === 'Cancelled'
            ? { operationId, isProcessing: false }
            : { operationId, isProcessing: true, stageKey: 'counting' }
        );
      }
    }
  };
};

const openDialog = (server, onFailed, { onError = () => undefined, serviceToRemove = null } = {}) =>
  bindLifted(effectSource, {
    gameToRemove: null,
    serviceToRemove,
    unmappedRemovalOpen: serviceToRemove === null,
    ApiService: server.api,
    classifyGameFromCacheInfo: () => {
      throw new Error('only the unmapped count runs here');
    },
    setCacheFileCount: () => undefined,
    setCountStage: () => undefined,
    setCountFailed: (failed) => {
      if (failed) onFailed();
    },
    setCacheFileBytes: () => undefined,
    setCountedUnmappedServices: () => undefined,
    setUnmappedRemovalError: (error) => {
      if (error !== null) onError(error);
    },
    COUNT_POLL_INTERVAL_MS: 5,
    COUNT_CANCEL_RETRY_MS: 60
  });

test('a reopened dialog starts its own count at once, even while the first start is unanswered', async () => {
  const server = createServer();
  let failed = false;
  const dialog = openDialog(server, () => {
    failed = true;
  });

  const closeFirst = dialog();
  await flush();
  closeFirst();
  const closeReopened = dialog();
  await flush();

  assert.deepEqual(
    [...server.statuses],
    [
      ['count-1', 'Waiting'],
      ['count-2', 'Waiting']
    ]
  );

  // The first start answers after its dialog closed: only that count is canceled.
  server.answerFirstStart();
  await flush();
  await flush();
  assert.equal(server.statuses.get('count-1'), 'Cancelled');
  assert.equal(server.statuses.get('count-2'), 'Waiting');
  assert.equal(failed, false);

  closeReopened();
  await flush();
  assert.equal(server.statuses.get('count-2'), 'Cancelled');
});

test('a cancel that fails once is sent again, so a closed dialog does not leave its count running', async () => {
  const server = createServer();
  let failures = 1;
  const cancel = server.api.cancelOperation;
  server.api.cancelOperation = (operationId) =>
    failures-- > 0 ? Promise.reject(new Error('timed out')) : cancel(operationId);
  const dialog = openDialog(server, () => undefined);

  const close = dialog();
  server.answerFirstStart();
  await flush();
  close();
  await waitForRetry();

  assert.equal(server.statuses.get('count-1'), 'Cancelled');
});

// Runs a dialog whose count cancels fail `failures` times, recording when each cancel was sent.
const failingCancels = (failures, serviceToRemove = null) => {
  const server = createServer();
  const sentAt = [];
  const errors = [];
  const cancel = server.api.cancelOperation;
  server.api.cancelOperation = (operationId) => {
    sentAt.push(Date.now());
    return failures-- > 0 ? Promise.reject(new Error('timed out')) : cancel(operationId);
  };
  const dialog = openDialog(server, () => undefined, {
    onError: (error) => errors.push(error),
    serviceToRemove
  });
  return { server, sentAt, errors, dialog };
};

test('the cancel retry waits before it is sent', async () => {
  const { server, sentAt, errors, dialog } = failingCancels(1);

  const close = dialog();
  server.answerFirstStart();
  await flush();
  close();
  await waitForRetry();

  assert.equal(sentAt.length, 2);
  assert.ok(sentAt[1] - sentAt[0] >= 50, `retry sent after ${sentAt[1] - sentAt[0]} ms`);
  assert.equal(server.statuses.get('count-1'), 'Cancelled');
  assert.deepEqual(errors, []);
});

test('a Retry whose abandoned count fails both cancels puts no error in the reopened dialog', async () => {
  const { server, sentAt, errors, dialog } = failingCancels(2);

  const closeFirst = dialog();
  server.answerFirstStart();
  await flush();
  // Retry: the effect cleans up the first count and starts a second in the same open dialog.
  closeFirst();
  const closeRetried = dialog();
  await waitForRetry();

  assert.equal(sentAt.length, 2);
  assert.equal(server.statuses.get('count-1'), 'Waiting');
  assert.equal(server.statuses.get('count-2'), 'Waiting');
  assert.deepEqual(errors, []);
  closeRetried();
});

test('a game or service dialog that fails both cancels sets no unmapped error', async () => {
  const { server, sentAt, errors, dialog } = failingCancels(2, { service_name: 'steam' });

  const close = dialog();
  server.answerFirstStart();
  await flush();
  close();
  await waitForRetry();

  assert.equal(sentAt.length, 2);
  assert.equal(server.statuses.get('count-1'), 'Waiting');
  assert.deepEqual(errors, []);
});
