import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { bindLifted, compileToUrl, liftHookCallback, moduleUrl } from './transpile-module.mjs';

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
