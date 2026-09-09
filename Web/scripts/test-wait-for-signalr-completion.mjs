import assert from 'node:assert/strict';
import test from 'node:test';
import {
  compileToUrl,
  compileTree,
  moduleUrl,
  MemoryStorage,
  notificationEvents
} from './transpile-module.mjs';

/**
 * Exercises the real waitForSignalRCompletion helper compiled from product source.
 * Uses a minimal fake SignalR bus (same pattern as test-signalr-seed-replay.mjs).
 */

globalThis.localStorage = new MemoryStorage();
globalThis.sessionStorage = new MemoryStorage();
const { rememberEvent } = await import(
  await compileTree('../src/contexts/notifications/handlers.ts', {
    '@/i18n': moduleUrl('export default {t:(key)=>key};')
  })
);

const createFakeSignalR = () => {
  const events = notificationEvents();
  const handlers = new Map();
  return {
    events,
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
      rememberEvent(
        events.current,
        event.startsWith('Service') || payload.operationType === 'serviceRemoval'
          ? 'service_removal'
          : 'game_removal',
        event === 'OperationWaitingComplete'
          ? 'handoff'
          : event.endsWith('Started')
            ? 'started'
            : event.endsWith('Progress')
              ? 'progress'
              : 'complete',
        event,
        payload
      );
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
    events: signalR.events,
    completeEvent: 'GameRemovalComplete',
    startedEvent: 'GameRemovalStarted',
    match: (payload) => payload?.operationId === opId,
    onStartedCapture: (payload) =>
      payload?.operationId === opId ? { opId: payload.operationId } : null,
    timeoutMs: 500
  });

  assert.equal(signalR.listenerCount('GameRemovalComplete'), 1);
  assert.equal(signalR.listenerCount('GameRemovalStarted'), 1);

  waitPromise.captureOperationId(opId, 'running');
  signalR.emit('GameRemovalStarted', { operationId: opId, gameAppId: 480 });
  signalR.emit('GameRemovalComplete', { operationId: opId, gameAppId: 480, success: true });

  const result = await waitPromise;
  assert.ok(result.event);
  assert.equal(result.event.operationId, opId);
  assert.equal(signalR.listenerCount('GameRemovalComplete'), 0);
});

test('an exact handoff rebinds the captured waiter operationId', async () => {
  const { waitForSignalRCompletion } = await loadWaitHelper();
  const signalR = createFakeSignalR();
  const waitingId = 'waiting-id';
  const runningId = 'running-id';
  const gameAppId = 12345;
  let capturedOpId = waitingId;

  const matchesIdentity = (payload) => payload?.gameAppId === gameAppId;

  const waitPromise = waitForSignalRCompletion({
    signalR,
    events: signalR.events,
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

  waitPromise.captureOperationId(waitingId, 'waiting');
  signalR.emit('OperationWaitingComplete', {
    operationId: waitingId,
    operationType: 'gameRemoval',
    promoted: true,
    cancelled: false,
    nextOperationId: runningId,
    nextStatus: 'running'
  });

  signalR.emit('GameRemovalStarted', { operationId: runningId, gameAppId });
  signalR.emit('GameRemovalComplete', { operationId: runningId, gameAppId, success: true });

  const result = await waitPromise;
  assert.ok(result.event, 'promoted Complete should resolve after the confirmed handoff');
  assert.equal(result.event.operationId, runningId);
});

test('a cancelled item still resolves through its own terminal event', async () => {
  const { waitForSignalRCompletion } = await loadWaitHelper();
  const signalR = createFakeSignalR();
  const opId = 'op-cancelled';

  const waitPromise = waitForSignalRCompletion({
    signalR,
    events: signalR.events,
    completeEvent: 'GameRemovalComplete',
    match: (payload) => payload?.operationId === opId,
    timeoutMs: 500
  });

  waitPromise.captureOperationId(opId, 'running');
  signalR.emit('GameRemovalComplete', { operationId: opId, success: false, cancelled: true });

  const result = await waitPromise;
  assert.equal(result.event.cancelled, true);
  assert.equal(signalR.listenerCount('GameRemovalComplete'), 0);
});

test('timeout resolves with timedOut', async () => {
  const { waitForSignalRCompletion } = await loadWaitHelper();
  const signalR = createFakeSignalR();

  const waitPromise = waitForSignalRCompletion({
    signalR,
    events: signalR.events,
    completeEvent: 'GameRemovalComplete',
    match: () => true,
    timeoutMs: 30
  });

  const result = await waitPromise;
  assert.equal(result.timedOut, true);
  assert.equal(signalR.listenerCount('GameRemovalComplete'), 0);
});

test('an item dequeued before promotion settles with dequeued', async () => {
  const { waitForSignalRCompletion } = await loadWaitHelper();
  const signalR = createFakeSignalR();
  const waitingId = 'wait-dequeued';

  const waitPromise = waitForSignalRCompletion({
    signalR,
    events: signalR.events,
    completeEvent: 'GameRemovalComplete',
    match: () => false,
    waitingOperationId: () => waitingId,
    timeoutMs: 500
  });

  waitPromise.captureOperationId(waitingId, 'waiting');
  signalR.emit('OperationWaitingComplete', {
    operationId: waitingId,
    operationType: 'gameRemoval',
    cancelled: true,
    promoted: false
  });

  const result = await waitPromise;
  assert.equal(result.dequeued.cancelled, true);
  assert.equal(result.event, undefined);
  assert.equal(signalR.listenerCount('OperationWaitingComplete'), 0);
});

test('promotion is not a dequeue - the wait stays open for the real completion', async () => {
  const { waitForSignalRCompletion } = await loadWaitHelper();
  const signalR = createFakeSignalR();
  const waitingId = 'wait-promoted';
  const runningId = 'run-promoted';

  const waitPromise = waitForSignalRCompletion({
    signalR,
    events: signalR.events,
    completeEvent: 'GameRemovalComplete',
    match: (payload) => payload?.operationId === runningId,
    waitingOperationId: () => waitingId,
    timeoutMs: 500
  });

  waitPromise.captureOperationId(waitingId, 'waiting');
  signalR.emit('OperationWaitingComplete', {
    operationId: waitingId,
    operationType: 'gameRemoval',
    cancelled: false,
    promoted: true,
    nextOperationId: runningId,
    nextStatus: 'running'
  });
  signalR.emit('GameRemovalComplete', { operationId: runningId, success: true });

  const result = await waitPromise;
  assert.ok(result.event, 'a promoted operation still emits its own completion');
  assert.equal(result.dequeued, undefined);
});
