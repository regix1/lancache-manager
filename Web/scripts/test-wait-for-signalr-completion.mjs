import assert from 'node:assert/strict';
import test from 'node:test';
import { compileToUrl } from './transpile-module.mjs';

/**
 * Exercises the real waitForSignalRCompletion helper compiled from product source.
 * Uses a minimal fake SignalR bus (same pattern as test-signalr-seed-replay.mjs).
 */

const createFakeSignalR = () => {
  const handlers = new Map();
  return {
    on(event, handler) {
      if (!handlers.has(event)) {
        handlers.set(event, new Set());
      }
      handlers.get(event).add(handler);
    },
    off(event, handler) {
      handlers.get(event)?.delete(handler);
    },
    emit(event, payload) {
      for (const handler of handlers.get(event) ?? []) {
        handler(payload);
      }
    },
    listenerCount(event) {
      return handlers.get(event)?.size ?? 0;
    }
  };
};

const loadWaitHelper = async () => {
  const moduleUrl = await compileToUrl('../src/contexts/notifications/waitForSignalRCompletion.ts');
  return import(moduleUrl);
};

test('registers listeners before POST and resolves on matching Complete', async () => {
  const { waitForSignalRCompletion } = await loadWaitHelper();
  const signalR = createFakeSignalR();
  const opId = 'op-immediate';

  const waitPromise = waitForSignalRCompletion({
    signalR,
    completeEvent: 'GameRemovalComplete',
    startedEvent: 'GameRemovalStarted',
    match: (payload) => payload?.operationId === opId,
    onStartedCapture: (payload) =>
      payload?.operationId === opId ? { opId: payload.operationId } : null,
    timeoutMs: 500
  });

  assert.equal(signalR.listenerCount('GameRemovalComplete'), 1);
  assert.equal(signalR.listenerCount('GameRemovalStarted'), 1);

  signalR.emit('GameRemovalStarted', { operationId: opId, gameAppId: 480 });
  signalR.emit('GameRemovalComplete', { operationId: opId, gameAppId: 480, success: true });

  const result = await waitPromise;
  assert.ok(result.event);
  assert.equal(result.event.operationId, opId);
  assert.equal(signalR.listenerCount('GameRemovalComplete'), 0);
});

test('onStartedCapture re-binds promoted operationId when match uses identity first', async () => {
  const { waitForSignalRCompletion } = await loadWaitHelper();
  const signalR = createFakeSignalR();
  const waitingId = 'waiting-id';
  const runningId = 'running-id';
  const gameAppId = 12345;
  let capturedOpId = waitingId;

  const matchesIdentity = (payload) => payload?.gameAppId === gameAppId;

  const waitPromise = waitForSignalRCompletion({
    signalR,
    completeEvent: 'GameRemovalComplete',
    startedEvent: 'GameRemovalStarted',
    match: (payload) => {
      if (capturedOpId) {
        return payload?.operationId === capturedOpId;
      }
      return matchesIdentity(payload);
    },
    onStartedCapture: (payload) =>
      matchesIdentity(payload) && typeof payload?.operationId === 'string'
        ? { opId: payload.operationId }
        : null,
    onOperationIdCaptured: (opId) => {
      capturedOpId = opId;
    },
    timeoutMs: 500
  });

  // Queued DELETE body pins the waiting id (bulk path today).
  capturedOpId = waitingId;

  signalR.emit('GameRemovalStarted', { operationId: runningId, gameAppId });
  signalR.emit('GameRemovalComplete', { operationId: runningId, gameAppId, success: true });

  const result = await waitPromise;
  assert.ok(result.event, 'promoted Complete should resolve after identity-based Started capture');
  assert.equal(result.event.operationId, runningId);
});

test('abort resolves with cancelled and detaches listeners', async () => {
  const { waitForSignalRCompletion } = await loadWaitHelper();
  const signalR = createFakeSignalR();
  const controller = new AbortController();

  const waitPromise = waitForSignalRCompletion({
    signalR,
    completeEvent: 'GameRemovalComplete',
    match: () => true,
    signal: controller.signal,
    timeoutMs: 500
  });

  controller.abort();
  const result = await waitPromise;
  assert.equal(result.cancelled, true);
  assert.equal(signalR.listenerCount('GameRemovalComplete'), 0);
});

test('timeout resolves with timedOut', async () => {
  const { waitForSignalRCompletion } = await loadWaitHelper();
  const signalR = createFakeSignalR();

  const waitPromise = waitForSignalRCompletion({
    signalR,
    completeEvent: 'GameRemovalComplete',
    match: () => true,
    timeoutMs: 30
  });

  const result = await waitPromise;
  assert.equal(result.timedOut, true);
  assert.equal(signalR.listenerCount('GameRemovalComplete'), 0);
});
