import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import {
  bindLifted,
  compileToUrl,
  liftConstArrow,
  liftHookCallback,
  moduleUrl,
  translator
} from './transpile-module.mjs';

/**
 * The setup depot step learns that a cloud import finished only from DepotMappingComplete. A client
 * whose socket dropped during the import never receives it, so the step re-reads the server's status
 * when the socket recovers. These cases run the shipped status check against stub answers, and run
 * the reconnect hook to prove a mount into a live socket reads status once.
 */

const componentPath = 'src/components/initialization/steps/DepotInitStep.tsx';
const checkSource = liftHookCallback(componentPath, 'useCallback', 'getPicsStatus');
const finishSource = liftHookCallback(componentPath, 'useCallback', 'mappingsImportedSuccess');

const buildStep = (status, selectedMethod) => {
  const state = {
    error: null,
    initializing: true,
    selectedMethod,
    downloadStatus: 'x',
    progress: 0
  };
  const calls = { onComplete: 0, reads: 0 };
  const timers = [];
  const bindings = {
    t: (key) => key,
    onComplete: () => {
      calls.onComplete += 1;
    },
    setDownloadStatus: (value) => {
      state.downloadStatus = value;
    },
    setProgress: (value) => {
      state.progress = value;
    },
    setInitializing: (value) => {
      state.initializing = value;
    },
    setSelectedMethod: (value) => {
      state.selectedMethod = value;
    },
    setError: (value) => {
      state.error = value;
    },
    completeTimeoutRef: { current: undefined },
    selectedMethodRef: { current: selectedMethod },
    // These cases cover a reload or a reconnect with no import request open in this tab.
    importRequestRef: { current: false },
    setTimeout: (run) => {
      const timer = { run, cleared: false };
      timers.push(timer);
      return timer;
    },
    clearTimeout: (timer) => {
      timer.cleared = true;
    },
    getErrorMessage: (error) => String(error),
    console
  };
  const finishImport = bindLifted(finishSource, bindings);
  const checkActiveOperation = bindLifted(checkSource, {
    ...bindings,
    finishImport,
    ApiService: {
      getPicsStatus: async () => {
        calls.reads += 1;
        return status;
      }
    }
  });
  const flushTimers = () =>
    timers.filter((timer) => !timer.cleared).forEach((timer) => timer.run());
  return { state, calls, finishImport, checkActiveOperation, flushTimers };
};

test('the step re-checks status whenever the socket recovers', () => {
  const source = readFileSync(new URL(`../${componentPath}`, import.meta.url), 'utf8');
  assert.match(source, /useReconnectRefetch\(signalR\.isConnected, checkActiveOperation\)/);
});

test('a cloud import that finished during the outage moves the step on once', async () => {
  const step = buildStep(
    { steamKit2: { isRebuildRunning: false }, database: { totalMappings: 42 } },
    'cloud'
  );
  await step.checkActiveOperation();
  step.flushTimers();

  assert.equal(step.state.downloadStatus, null);
  assert.equal(step.state.progress, 100);
  assert.equal(step.calls.onComplete, 1);
});

test('a success event and a re-check inside its delay still finish once', async () => {
  const step = buildStep(
    { steamKit2: { isRebuildRunning: false }, database: { totalMappings: 42 } },
    'cloud'
  );
  step.finishImport();
  await step.checkActiveOperation();
  step.flushTimers();

  assert.equal(step.calls.onComplete, 1);
});

test('a cloud import that ended with no mappings shows the failure', async () => {
  const step = buildStep(
    { steamKit2: { isRebuildRunning: false }, database: { totalMappings: 0 } },
    'cloud'
  );
  await step.checkActiveOperation();
  step.flushTimers();

  assert.equal(step.state.error, 'initialization.depotInit.failedToDownload');
  assert.equal(step.state.initializing, false);
  assert.equal(step.state.selectedMethod, null);
  assert.equal(step.calls.onComplete, 0);
});

test('an import still running keeps the step in progress', async () => {
  const step = buildStep(
    { steamKit2: { isRebuildRunning: true }, database: { totalMappings: 42 } },
    'cloud'
  );
  await step.checkActiveOperation();
  step.flushTimers();

  assert.equal(step.state.initializing, true);
  assert.equal(step.state.downloadStatus, 'initialization.depotInit.operationInProgress');
  assert.equal(step.calls.onComplete, 0);
});

test('a mount into a live socket reads status once, with no reconnect read', async () => {
  const reactStubUrl = moduleUrl(`
    const slots = [];
    let cursor = 0;
    export const begin = () => { cursor = 0; };
    export const useRef = (initial) => (slots[cursor++] ??= { current: initial });
    export const useEffect = (run, deps) => {
      const slot = (slots[cursor++] ??= { deps: null });
      if (!slot.deps || deps.some((value, index) => !Object.is(value, slot.deps[index]))) {
        slot.deps = deps;
        run();
      }
    };
  `);
  const { begin } = await import(reactStubUrl);
  const { useReconnectRefetch } = await import(
    await compileToUrl('../src/hooks/useReconnectRefetch.ts', { react: reactStubUrl })
  );
  const step = buildStep(
    { steamKit2: { isRebuildRunning: false }, database: { totalMappings: 0 } },
    null
  );
  const render = (isConnected) => {
    begin();
    useReconnectRefetch(isConnected, step.checkActiveOperation);
  };

  // The mount check runs from the component's own effect.
  await step.checkActiveOperation();
  render(true);
  render(true);
  await new Promise((resolve) => setTimeout(resolve, 0));

  assert.equal(step.calls.reads, 1);
  assert.equal(step.state.error, null, 'with no method chosen the check leaves the step alone');
});

const t = translator('zh');

globalThis.testTranslator = { t };
const i18nStub = moduleUrl('export default globalThis.testTranslator;');

// The real constants module reads `import.meta.env` while it loads, which only vite supplies.
// `apiError.ts` reads APP_EVENTS from it on the 401 branch alone, and nothing here sends a 401.
const apiErrorUrl = await compileToUrl('../src/services/apiError.ts', {
  '@/i18n': i18nStub,
  '@utils/constants': moduleUrl('export const APP_EVENTS = {};')
});
const { ApiError } = await import(apiErrorUrl);
const { getErrorMessage } = await import(
  await compileToUrl('../src/utils/error.ts', {
    '@/i18n': i18nStub,
    '../services/apiError': apiErrorUrl
  })
);

const failedImportEvent = {
  scanMode: 'github',
  success: false,
  stageKey: 'signalr.depotMapping.github.invalidJson',
  context: {},
  error: 'Downloaded depot mapping file was not valid JSON',
  message: 'x'
};

/** The step's SignalR handlers and its download click, sharing one piece of state. */
const buildLiveStep = () => {
  const state = { error: null, downloadStatus: null, progress: 0 };
  const handlers = {};
  let rejectRequest;
  const bindings = {
    t,
    signalR: {
      on: (name, handler) => {
        handlers[name] = handler;
      },
      off: () => undefined
    },
    setInitializing: () => undefined,
    setSelectedMethod: () => undefined,
    setProgress: (value) => {
      state.progress = value;
    },
    setDownloadStatus: (value) => {
      state.downloadStatus = value;
    },
    setError: (value) => {
      state.error = typeof value === 'function' ? value(state.error) : value;
    },
    finishImport: () => undefined,
    checkActiveOperation: () => undefined,
    selectedMethodRef: { current: 'cloud' },
    importRequestRef: { current: false },
    ApiService: {
      downloadPrecreatedDepotData: () =>
        new Promise((_, reject) => {
          rejectRequest = reject;
        })
    },
    ApiError,
    isAbortError: () => false,
    getErrorMessage
  };
  bindLifted(
    liftHookCallback(componentPath, 'useEffect', 'handleDepotMappingComplete'),
    bindings
  )();
  const describeError = bindLifted(liftConstArrow(componentPath, 'describeError'), bindings);
  const handleDownload = bindLifted(liftConstArrow(componentPath, 'handleDownload'), {
    ...bindings,
    describeError
  });
  const click = () => {
    const done = handleDownload();
    return { done, reject: (error) => rejectRequest(error) };
  };
  return { state, handlers, click };
};

const importFailedResponse = () =>
  new ApiError({
    message: 'Failed to download and import pre-created depot data from GitHub',
    status: 503,
    kind: 'http',
    body: { stageKey: 'errors.depot.githubImportFailed' }
  });

test('a failed import event shows the Chinese sentence for its cause', () => {
  const step = buildLiveStep();
  step.handlers.DepotMappingComplete(failedImportEvent);

  assert.equal(step.state.error, t('signalr.depotMapping.github.invalidJson'));
});

test('the started and progress events show the Chinese download sentence', () => {
  const step = buildLiveStep();
  step.handlers.DepotMappingStarted({
    scanMode: 'github',
    stageKey: 'signalr.depotMapping.github.downloading',
    context: {},
    message: 'Downloading depot mappings from GitHub...'
  });
  assert.equal(step.state.downloadStatus, t('signalr.depotMapping.github.downloading'));

  step.state.downloadStatus = null;
  step.handlers.DepotMappingProgress({
    stageKey: 'signalr.depotMapping.github.downloading',
    percentComplete: 40,
    context: { status: 'Parsing JSON' },
    message: 'Parsing JSON'
  });
  assert.equal(step.state.downloadStatus, t('signalr.depotMapping.github.downloading'));
  assert.equal(step.state.progress, 40);
});

test('the failure event keeps its sentence when the request then reports the failure', async () => {
  const step = buildLiveStep();
  const request = step.click();
  step.handlers.DepotMappingComplete(failedImportEvent);
  request.reject(importFailedResponse());
  await request.done;

  assert.equal(step.state.error, t('signalr.depotMapping.github.invalidJson'));
});

test('a request failure followed by the failure event ends on the event sentence', async () => {
  const step = buildLiveStep();
  const request = step.click();
  request.reject(importFailedResponse());
  await request.done;
  step.handlers.DepotMappingComplete(failedImportEvent);

  assert.equal(step.state.error, t('signalr.depotMapping.github.invalidJson'));
});

test('a conflict with no event shows the conflict sentence', async () => {
  const step = buildLiveStep();
  const request = step.click();
  request.reject(new ApiError({ message: 'conflict', status: 409, kind: 'conflict', body: null }));
  await request.done;

  assert.equal(step.state.error, t('errors.conflict.duplicate'));
});
