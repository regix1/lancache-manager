import assert from 'node:assert/strict';
import test from 'node:test';
import { readFile } from 'node:fs/promises';
import { compileToUrl, moduleUrl, notificationEvents } from './transpile-module.mjs';

const { decideScanHold, isConfirmedScanRefusalStatus, readScanAdmission } = await import(
  await compileToUrl('../src/components/features/management/game-detection/scanAdmission.ts', {
    '@contexts/notifications/notificationStatus': await compileToUrl(
      '../src/contexts/notifications/notificationStatus.ts'
    )
  })
);

const notificationStatusUrl = await compileToUrl(
  '../src/contexts/notifications/notificationStatus.ts'
);
const scanAdmissionUrl = await compileToUrl(
  '../src/components/features/management/game-detection/scanAdmission.ts',
  { '@contexts/notifications/notificationStatus': notificationStatusUrl }
);
const waitUrl = await compileToUrl('../src/contexts/notifications/waitForSignalRCompletion.ts');
const apiUrl = moduleUrl(`
  export default {
    getTrackedOperation: (...args) => globalThis.__scanApi.getTrackedOperation(...args),
    getWaitingOperations: (...args) => globalThis.__scanApi.getWaitingOperations(...args),
    getActiveGameDetection: (...args) => globalThis.__scanApi.getActiveGameDetection(...args),
    getEvictionScanStatus: (...args) => globalThis.__scanApi.getEvictionScanStatus(...args)
  };
`);
const apiErrorUrl = moduleUrl(`
  export class ApiError extends Error {
    constructor(status) { super(String(status)); this.status = status; }
  }
`);
const { followAdmittedScan, watchScanHold } = await import(
  await compileToUrl('../src/components/features/management/game-detection/scanHoldRecovery.ts', {
    '@services/api.service': apiUrl,
    '@services/apiError': apiErrorUrl,
    '@contexts/notifications/waitForSignalRCompletion': waitUrl,
    './scanAdmission': scanAdmissionUrl
  })
);

const idleApi = () => ({
  getTrackedOperation: async (operationId) => ({ id: operationId, active: false }),
  getWaitingOperations: async () => [],
  getActiveGameDetection: async () => ({ isProcessing: false, operation: null }),
  getEvictionScanStatus: async () => ({ isProcessing: false, operationId: null })
});

const createSignalR = () => {
  const handlers = new Map();
  return {
    events: notificationEvents(),
    on(name, handler) {
      if (!handlers.has(name)) handlers.set(name, new Set());
      handlers.get(name).add(handler);
    },
    off(name, handler) {
      handlers.get(name)?.delete(handler);
    },
    emit(name, event) {
      for (const handler of handlers.get(name) ?? []) handler(event);
    },
    listenerCount(name) {
      return handlers.get(name)?.size ?? 0;
    }
  };
};

test('a waiting status holds even when the active flag is false', () => {
  assert.equal(
    decideScanHold({
      operation: { status: 'waiting', nextOperationId: null, nextStatus: null },
      waitingListed: false,
      activeScanMatches: false,
      endpointFailed: false
    }),
    'hold'
  );
});

test('a terminal status releases', () => {
  assert.equal(
    decideScanHold({
      operation: { status: 'completed' },
      waitingListed: false,
      activeScanMatches: false,
      endpointFailed: false
    }),
    'release'
  );
});

test('a terminal waiting record holds while its successor scan is active', () => {
  assert.equal(
    decideScanHold({
      operation: { status: 'completed' },
      waitingListed: false,
      activeScanMatches: true,
      endpointFailed: false
    }),
    'hold'
  );
});

test('a missing operation with no waiting or active scan releases', () => {
  assert.equal(
    decideScanHold({
      operation: null,
      waitingListed: false,
      activeScanMatches: false,
      endpointFailed: false
    }),
    'release'
  );
});

test('a failed recovery stays unknown', () => {
  assert.equal(
    decideScanHold({
      operation: null,
      waitingListed: false,
      activeScanMatches: false,
      endpointFailed: true
    }),
    'unknown'
  );
});

test('a busy successor holds', () => {
  assert.equal(
    decideScanHold({
      operation: { status: 'completed', nextOperationId: 'next', nextStatus: 'running' },
      waitingListed: false,
      activeScanMatches: false,
      endpointFailed: false
    }),
    'hold'
  );
});

test('only a 400 is a confirmed scan refusal', () => {
  assert.equal(isConfirmedScanRefusalStatus(400), true);
  assert.equal(isConfirmedScanRefusalStatus(undefined), false);
  assert.equal(readScanAdmission({ queued: true, operationId: 'a' }), 'queued');
  assert.equal(readScanAdmission({ alreadyRunning: true, operationId: 'a' }), 'alreadyRunning');
  assert.equal(readScanAdmission({ operationId: 'a' }), 'started');
});

test('a recovered hold is polled until its operation becomes terminal', async () => {
  let reads = 0;
  globalThis.__scanApi = {
    ...idleApi(),
    getTrackedOperation: async (operationId) => {
      reads += 1;
      return {
        id: operationId,
        active: reads === 1,
        status: reads === 1 ? 'running' : 'completed'
      };
    }
  };

  const result = await watchScanHold({
    operationId: 'scan-1',
    kind: 'gameDetection',
    abortSignal: new AbortController().signal,
    pollMs: 1
  });

  assert.equal(result.decision, 'release');
  assert.equal(result.operationId, 'scan-1');
  assert.equal(reads, 2);
});

test('mount recovery adopts a hidden active scan before following it', async () => {
  let activeReads = 0;
  globalThis.__scanApi = {
    ...idleApi(),
    getTrackedOperation: async (operationId) => ({
      id: operationId,
      active: false,
      status: 'completed'
    }),
    getActiveGameDetection: async () => {
      activeReads += 1;
      return activeReads === 1
        ? {
            isProcessing: true,
            operation: { operationId: 'hidden-scan', parentOperationId: 'eviction-parent' }
          }
        : { isProcessing: false, operation: null };
    }
  };

  const result = await watchScanHold({
    operationId: null,
    kind: 'gameDetection',
    abortSignal: new AbortController().signal,
    pollMs: 1
  });

  assert.equal(result.decision, 'release');
  assert.equal(result.operationId, 'hidden-scan');
});

test('eviction recovery adopts the running scan that replaced its waiting operation', async () => {
  let operationReads = 0;
  globalThis.__scanApi = {
    ...idleApi(),
    getTrackedOperation: async (operationId) => {
      operationReads += 1;
      return operationReads === 1
        ? { id: operationId, active: false }
        : { id: operationId, active: false, status: 'completed' };
    },
    getEvictionScanStatus: async () =>
      operationReads === 1
        ? {
            isProcessing: true,
            operationId: 'eviction-running',
            previousOperationId: 'eviction-waiting'
          }
        : { isProcessing: false, operationId: null, previousOperationId: null }
  };

  const result = await watchScanHold({
    operationId: 'eviction-waiting',
    kind: 'evictionScan',
    abortSignal: new AbortController().signal,
    pollMs: 1
  });

  assert.equal(result.decision, 'release');
  assert.equal(result.operationId, 'eviction-running');
  assert.equal(operationReads, 2);
});

test('a completed waiting operation holds while its successor scan is running', async () => {
  let reads = 0;
  globalThis.__scanApi = {
    ...idleApi(),
    getTrackedOperation: async (operationId) => {
      reads += 1;
      if (operationId === 'eviction-waiting') {
        return { id: operationId, active: false, status: 'completed' };
      }
      return reads < 4
        ? { id: operationId, active: true, status: 'running' }
        : { id: operationId, active: false, status: 'completed' };
    },
    getEvictionScanStatus: async () =>
      reads < 4
        ? {
            isProcessing: true,
            operationId: 'eviction-running',
            previousOperationId: 'eviction-waiting'
          }
        : { isProcessing: false, operationId: null, previousOperationId: null }
  };

  const result = await watchScanHold({
    operationId: 'eviction-waiting',
    kind: 'evictionScan',
    abortSignal: new AbortController().signal,
    pollMs: 1
  });

  assert.equal(result.decision, 'release');
  assert.equal(result.operationId, 'eviction-running');
  assert.ok(reads > 1);
});

test('the page watcher stops on an uncertain read so its caller can retry', async () => {
  let reads = 0;
  globalThis.__scanApi = {
    ...idleApi(),
    getTrackedOperation: async () => {
      reads += 1;
      throw new Error('offline');
    }
  };

  const result = await watchScanHold({
    operationId: 'scan-unknown',
    kind: 'gameDetection',
    abortSignal: new AbortController().signal,
    pollMs: 1
  });

  assert.equal(result.decision, 'unknown');
  assert.equal(reads, 1);
});

test('unmount aborts a recovery poll without another endpoint read', async () => {
  let reads = 0;
  globalThis.__scanApi = {
    ...idleApi(),
    getTrackedOperation: async (operationId) => {
      reads += 1;
      return { id: operationId, active: true, status: 'running' };
    }
  };
  const abort = new AbortController();
  const resultPromise = watchScanHold({
    operationId: 'scan-running',
    kind: 'gameDetection',
    abortSignal: abort.signal,
    pollMs: 1000
  });
  setTimeout(() => abort.abort(), 5);

  const result = await resultPromise;
  assert.equal(result.decision, 'aborted');
  assert.equal(reads, 1);
});

test('an eviction Started predecessor releases the queued hold without WaitingComplete', async () => {
  const signalR = createSignalR();
  globalThis.__scanApi = {
    ...idleApi(),
    getTrackedOperation: async (operationId) => ({
      id: operationId,
      active: false,
      status: 'waiting'
    }),
    getWaitingOperations: async () => [
      { operationId: 'eviction-waiting', operationType: 'evictionScan' }
    ],
    getEvictionScanStatus: async () => ({
      isProcessing: true,
      operationId: 'eviction-running'
    })
  };

  const resultPromise = followAdmittedScan({
    signalR,
    events: signalR.events,
    abortSignal: new AbortController().signal,
    operationId: 'eviction-waiting',
    admission: 'queued',
    completeEvent: 'EvictionScanComplete',
    kind: 'evictionScan'
  });
  signalR.emit('EvictionScanStarted', {
    operationId: 'eviction-running',
    previousOperationId: 'eviction-waiting'
  });
  signalR.emit('EvictionScanComplete', {
    operationId: 'eviction-running',
    success: true
  });

  assert.equal(await resultPromise, 'release');
  assert.equal(signalR.listenerCount('EvictionScanComplete'), 0);
  assert.equal(signalR.listenerCount('EvictionScanStarted'), 0);
  assert.equal(signalR.listenerCount('EvictionScanProgress'), 0);
});

for (const eventName of ['EvictionScanProgress', 'EvictionScanComplete']) {
  test(`${eventName} rebinds a queued eviction hold without Started`, async () => {
    const signalR = createSignalR();
    globalThis.__scanApi = {
      ...idleApi(),
      getTrackedOperation: async () => {
        throw new Error('offline');
      }
    };

    const resultPromise = followAdmittedScan({
      signalR,
      events: signalR.events,
      abortSignal: new AbortController().signal,
      operationId: 'eviction-waiting',
      admission: 'queued',
      completeEvent: 'EvictionScanComplete',
      kind: 'evictionScan'
    });
    signalR.emit(eventName, {
      operationId: 'eviction-running',
      previousOperationId: 'eviction-waiting',
      success: true
    });
    if (eventName !== 'EvictionScanComplete') {
      signalR.emit('EvictionScanComplete', {
        operationId: 'eviction-running',
        success: true
      });
    }

    assert.equal(await resultPromise, 'release');
    assert.equal(signalR.listenerCount('EvictionScanComplete'), 0);
  });
}

test('a failed recovery read does not abandon the SignalR wait', async () => {
  const signalR = createSignalR();
  globalThis.__scanApi = {
    ...idleApi(),
    getTrackedOperation: async () => {
      throw new Error('offline');
    }
  };

  const resultPromise = followAdmittedScan({
    signalR,
    events: signalR.events,
    abortSignal: new AbortController().signal,
    operationId: 'eviction-running',
    admission: 'started',
    completeEvent: 'EvictionScanComplete',
    kind: 'evictionScan'
  });
  signalR.emit('EvictionScanComplete', {
    operationId: 'eviction-running',
    success: true
  });

  assert.equal(await resultPromise, 'release');
  assert.equal(signalR.listenerCount('EvictionScanComplete'), 0);
});

test('authoritative recovery releases a scan without waiting for a SignalR timeout', async () => {
  const signalR = createSignalR();
  globalThis.__scanApi = {
    ...idleApi(),
    getTrackedOperation: async (operationId) => ({
      id: operationId,
      active: false,
      status: 'completed'
    })
  };

  const result = await Promise.race([
    followAdmittedScan({
      signalR,
      events: signalR.events,
      abortSignal: new AbortController().signal,
      operationId: 'eviction-completed',
      admission: 'started',
      completeEvent: 'EvictionScanComplete',
      kind: 'evictionScan'
    }),
    new Promise((_, reject) =>
      setTimeout(() => reject(new Error('recovery did not start with the SignalR wait')), 100)
    )
  ]);

  assert.equal(result, 'release');
  assert.equal(signalR.listenerCount('EvictionScanComplete'), 0);
});

test('management components hydrate scan holds and show eviction kickoff progress', async () => {
  const [game, storage] = await Promise.all([
    readFile(
      new URL(
        '../src/components/features/management/game-detection/GameCacheDetector.tsx',
        import.meta.url
      ),
      'utf8'
    ),
    readFile(
      new URL('../src/components/features/management/sections/StorageSection.tsx', import.meta.url),
      'utf8'
    )
  ]);

  assert.match(game, /useEffect\(\(\) => \{\s+void recoverDetectionAdmission\(\)/);
  assert.match(
    game,
    /useReconnectRefetch\(isConnected, \(\) => \{\s+void recoverDetectionAdmission\(\)/
  );
  assert.doesNotMatch(game, /if \(isStartingDetection\) \{/);
  assert.match(storage, /useEffect\(\(\) => \{\s+void recoverEvictionAdmission\(\)/);
  assert.match(storage, /const confirmation = await recoverScanHold/);
  assert.match(storage, /isStartingEvictionScan \? \([\s\S]*?<LoadingSpinner inline size="xs"/);
});
