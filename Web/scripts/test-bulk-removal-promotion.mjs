import assert from 'node:assert/strict';
import test from 'node:test';
import { compileToUrl } from './transpile-module.mjs';

/**
 * Regression tests for bulk cache game removal under the operation wait-queue.
 * Uses the real gameRemovalEntity matchers compiled from product source.
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
    }
  };
};

const loadWaitHelper = async () => {
  const moduleUrl = await compileToUrl('../src/contexts/notifications/waitForSignalRCompletion.ts');
  return import(moduleUrl);
};

const loadEntity = async () => {
  const moduleUrl = await compileToUrl(
    '../src/components/features/management/game-detection/gameRemovalEntity.ts'
  );
  return import(moduleUrl);
};

const GAME_FIXTURES = [
  {
    label: 'steam',
    game: { game_app_id: 570, game_name: 'Dota 2', service: 'steam' },
    startedPayload(game, runningId) {
      return {
        operationId: runningId,
        gameAppId: game.game_app_id,
        epicAppId: null,
        gameName: game.game_name
      };
    },
    completePayload(game, runningId) {
      return {
        operationId: runningId,
        gameAppId: game.game_app_id,
        epicAppId: null,
        gameName: game.game_name,
        success: true
      };
    }
  },
  {
    label: 'epic',
    game: {
      game_app_id: 0,
      game_name: 'Fortnite',
      service: 'epicgames',
      epic_app_id: 'cat-fortnite'
    },
    startedPayload(game, runningId) {
      return {
        operationId: runningId,
        gameAppId: null,
        epicAppId: game.epic_app_id,
        gameName: game.game_name
      };
    },
    completePayload(game, runningId) {
      return {
        operationId: runningId,
        gameAppId: null,
        epicAppId: game.epic_app_id,
        gameName: game.game_name,
        success: true
      };
    }
  },
  {
    label: 'named blizzard',
    game: { game_app_id: 0, game_name: 'Diablo IV', service: 'blizzard' },
    startedPayload(game, runningId) {
      return {
        operationId: runningId,
        gameAppId: null,
        epicAppId: null,
        gameName: game.game_name
      };
    },
    completePayload(game, runningId) {
      return {
        operationId: runningId,
        gameAppId: null,
        epicAppId: null,
        gameName: game.game_name,
        success: true
      };
    }
  }
];

const createBulkCacheCorrelation = (game, waitHelper, entityHelper) => {
  const { waitForSignalRCompletion } = waitHelper;
  const {
    classifyGameFromCacheInfo,
    matchesGameRemovalIdentity,
    matchesGameRemovalComplete,
    shouldPinOperationIdFromResponse
  } = entityHelper;
  const entity = classifyGameFromCacheInfo(game);
  let currentOperationId = null;

  const startWait = (signalR, timeoutMs) =>
    waitForSignalRCompletion({
      signalR,
      completeEvent: 'GameRemovalComplete',
      startedEvent: 'GameRemovalStarted',
      match: (payload) => matchesGameRemovalComplete(payload, entity, currentOperationId),
      onStartedCapture: (payload) =>
        matchesGameRemovalIdentity(payload, entity) && typeof payload.operationId === 'string'
          ? { opId: payload.operationId }
          : null,
      onOperationIdCaptured: (opId) => {
        currentOperationId = opId;
      },
      timeoutMs
    });

  return {
    async waitForPromotion(signalR, fixture, { waitingId, runningId, timeoutMs = 80 }) {
      const waitPromise = startWait(signalR, timeoutMs);
      const response = { operationId: waitingId, queued: true, alreadyRunning: false };
      if (shouldPinOperationIdFromResponse(response)) {
        currentOperationId = response.operationId;
      }
      signalR.emit('GameRemovalStarted', fixture.startedPayload(game, runningId));
      signalR.emit('GameRemovalComplete', fixture.completePayload(game, runningId));
      return waitPromise;
    },

    async waitForImmediateStart(signalR, fixture, { runningId, timeoutMs = 80 }) {
      const waitPromise = startWait(signalR, timeoutMs);
      const response = { operationId: runningId, queued: false, alreadyRunning: false };
      if (shouldPinOperationIdFromResponse(response)) {
        currentOperationId = response.operationId;
      }
      signalR.emit('GameRemovalStarted', fixture.startedPayload(game, runningId));
      signalR.emit('GameRemovalComplete', fixture.completePayload(game, runningId));
      return waitPromise;
    }
  };
};

test('identity-first correlation resolves queued promotion for all platforms', async () => {
  const waitHelper = await loadWaitHelper();
  const entityHelper = await loadEntity();

  for (const fixture of GAME_FIXTURES) {
    const signalR = createFakeSignalR();
    const correlation = createBulkCacheCorrelation(fixture.game, waitHelper, entityHelper);

    const result = await correlation.waitForPromotion(signalR, fixture, {
      waitingId: `wait-${fixture.label}`,
      runningId: `run-${fixture.label}`
    });

    assert.ok(
      result.event,
      `${fixture.label}: identity-first Started capture should bind promoted opId`
    );
    assert.equal(result.event.operationId, `run-${fixture.label}`, fixture.label);
  }
});

test('bulk cache correlation still works for immediate start (no queue)', async () => {
  const waitHelper = await loadWaitHelper();
  const entityHelper = await loadEntity();
  const fixture = GAME_FIXTURES[0];
  const signalR = createFakeSignalR();
  const correlation = createBulkCacheCorrelation(fixture.game, waitHelper, entityHelper);
  const runningId = 'run-immediate';

  const result = await correlation.waitForImmediateStart(signalR, fixture, { runningId });

  assert.ok(result.event, 'immediate start uses same id on HTTP and SignalR');
  assert.equal(result.event.operationId, runningId);
});

test('regression gate — bulk cache resolves after queue promotion', async () => {
  const waitHelper = await loadWaitHelper();
  const entityHelper = await loadEntity();
  const fixture = GAME_FIXTURES[0];
  const signalR = createFakeSignalR();
  const correlation = createBulkCacheCorrelation(fixture.game, waitHelper, entityHelper);

  const result = await correlation.waitForPromotion(signalR, fixture, {
    waitingId: 'wait-1111',
    runningId: 'run-2222'
  });

  assert.ok(result.event, 'bulk cache game wait should resolve after queue promotion');
  assert.equal(result.event.operationId, 'run-2222');
});
