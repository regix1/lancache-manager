import assert from 'node:assert/strict';
import test from 'node:test';
import ts from 'typescript';
import { bindLifted, findSoleNode, parseSource } from './transpile-module.mjs';

/**
 * Cancel on the setup wizard's log step force stops the pass. A pass that saved its success just
 * before the stop still completes, so the "cancelled" notice has to come from the completion event
 * (its `cancelled` flag), never from the click, and a success must clear any notice.
 */

const stepFile = 'src/components/initialization/steps/LogProcessingStep.tsx';
const source = parseSource(stepFile, ts.ScriptKind.TSX);

const handlerText = (name) =>
  findSoleNode(
    source,
    `${name} declaration`,
    (node) => ts.isVariableDeclaration(node) && node.name.getText(source) === name
  ).getText(source);

test('the cancel click does not write the cancelled notice', () => {
  assert.ok(
    !handlerText('handleCancelProcessing').includes('initialization.logProcessing.cancelled'),
    'the notice comes from the completion event, not from the click'
  );
});

test('the cancel click leaves the step running until the pass reports its ending', () => {
  assert.ok(
    !handlerText('handleCancelProcessing').includes('setProcessing(false)'),
    'the completion event or the watchdog ends the running view, not the click'
  );
});

test("the watchdog reads the pass's own ending", () => {
  const watchdogEffect = findSoleNode(
    source,
    'watchdog effect',
    (node) =>
      ts.isCallExpression(node) &&
      node.getText(source).startsWith('useEffect(') &&
      node.getText(source).includes('const watchdog = setInterval')
  ).getText(source);
  assert.ok(watchdogEffect.includes('getTrackedOperation'), 'the run says how the pass ended');
  assert.ok(
    watchdogEffect.includes('initialization.logProcessing.cancelled'),
    'a canceled run shows the canceled notice'
  );
});

test('a successful completion clears the notice', () => {
  const completion = handlerText('handleLogProcessingComplete');
  const successBranch = completion.slice(completion.indexOf('// Success case'));
  assert.ok(successBranch.includes('setNotice(null)'), 'a finished pass shows no notice');
});

// The component runs for real below: its hooks, timers and clock are replaced by a small renderer with a fake clock,
// so the watchdog's 5 second ticks are stepped instead of waited for.
const elements = (tree) =>
  !tree || typeof tree !== 'object'
    ? []
    : [tree, ...(tree.props?.children ?? []).flatMap(elements)];
const settle = () => new Promise((resolve) => setImmediate(resolve));

const mount = (name, extra) => {
  const slots = [];
  const passive = new Map();
  const timers = new Map();
  let cursor = 0;
  let dirty = false;
  let tree;
  let props = {};
  let now = 0;
  let nextTimer = 0;
  const equal = (a, b) =>
    a !== undefined &&
    b !== undefined &&
    a.length === b.length &&
    a.every((value, index) => Object.is(value, b[index]));
  const useState = (initial) => {
    const index = cursor++;
    slots[index] ??= { value: typeof initial === 'function' ? initial() : initial };
    return [
      slots[index].value,
      (update) => {
        const value = typeof update === 'function' ? update(slots[index].value) : update;
        if (!Object.is(value, slots[index].value)) {
          slots[index].value = value;
          dirty = true;
        }
      }
    ];
  };
  const useRef = (initial) => {
    const index = cursor++;
    slots[index] ??= { current: initial };
    return slots[index];
  };
  const useEffect = (setup, deps) => {
    const index = cursor++;
    const slot = (slots[index] ??= {});
    if (!equal(slot.deps, deps)) {
      slot.deps = deps;
      passive.set(index, setup);
    }
  };
  const setInterval = (callback, delay) => {
    const id = ++nextTimer;
    timers.set(id, { callback, due: now + delay, interval: delay });
    return id;
  };
  const clearInterval = (id) => timers.delete(id);
  const React = {
    createElement: (type, attributes, ...children) => ({
      type,
      props: { ...attributes, children: children.flat(Infinity) }
    }),
    // The log step reads its refs through `React.useRef`.
    useRef
  };
  const bindings = {
    React,
    useState,
    useEffect,
    setInterval,
    clearInterval,
    Date: { now: () => now },
    ...extra
  };
  const component = bindLifted(
    findSoleNode(
      source,
      `${name} declaration`,
      (node) => ts.isVariableDeclaration(node) && node.name.getText(source) === name
    ).initializer.getText(source),
    bindings,
    { jsx: ts.JsxEmit.React }
  );
  const commit = () => {
    let passes = 0;
    do {
      assert.ok(++passes < 30, 'updates settle');
      dirty = false;
      cursor = 0;
      tree = component(props);
    } while (dirty);
    return tree;
  };
  const flushPassive = () => {
    let passes = 0;
    while (passive.size || dirty) {
      assert.ok(++passes < 30, 'passive updates settle');
      const pending = [...passive];
      passive.clear();
      for (const [index, setup] of pending) {
        slots[index].cleanup?.();
        slots[index].cleanup = setup();
      }
      if (dirty) commit();
    }
    return tree;
  };
  return {
    get tree() {
      return tree;
    },
    get now() {
      return now;
    },
    render(next = props) {
      flushPassive();
      props = next;
      return commit();
    },
    event(callback) {
      flushPassive();
      callback();
      return commit();
    },
    flushPassive,
    advance(ms) {
      flushPassive();
      const end = now + ms;
      for (;;) {
        const next = [...timers].sort((a, b) => a[1].due - b[1].due || a[0] - b[0])[0];
        if (!next || next[1].due > end) break;
        now = next[1].due;
        next[1].due += next[1].interval;
        next[1].callback();
        if (dirty) commit();
        flushPassive();
      }
      now = end;
      return tree;
    },
    dispose() {
      flushPassive();
      slots.forEach((slot) => slot.cleanup?.());
    }
  };
};

// The server stub copies the real endpoints. The tracker drops a completed or canceled run 10 seconds after it ends
// (UnifiedOperationTracker.cs) and keeps a failed one; a dropped run still answers its status for 5 minutes after the
// drop, and after that GET /api/operations/{id} answers 200 with no status (OperationsController.cs); POST /api/operations/{id}/force-kill answers 404 "Operation not found or already
// completed" once the run is gone (OperationsController.cs, OperationCancellationService.cs). The real ApiError
// carries the HTTP status (apiError.ts).
class ApiError extends Error {
  constructor(status, message) {
    super(message);
    this.status = status;
  }
}

async function startWizard(overrides) {
  const server = {
    processing: true,
    // One entry per run id: `{ status, endedAt, nextOperationId? }`, as the tracker keeps each run by its own id.
    runs: new Map([['op', { status: 'running', endedAt: 0 }]]),
    asked: [],
    killed: [],
    runGone: false,
    endingOnForceStop: 'cancelled',
    operationId: 'op',
    ...overrides
  };
  const ref = {};
  const handlers = new Map();
  let runGate = null;
  // The hook returns the same objects on every render, so the step's effects run once, as in the app.
  const translation = { t: (key) => key };
  const hub = {
    on: (event, handler) => handlers.set(event, handler),
    off: () => undefined
  };
  const runner = mount('LogProcessingStep', {
    useTranslation: () => translation,
    useSignalR: () => hub,
    useConfig: () => ({ config: { dataSources: [] } }),
    useSelectionSet: () => ({ selected: new Set(), toggle: () => undefined }),
    ApiError,
    ApiService: {
      getProcessingStatus: () =>
        Promise.resolve(
          server.processing
            ? {
                isProcessing: true,
                operationId: server.operationId,
                progress: 40,
                status: 'processing'
              }
            : { isProcessing: false, operationId: null, status: 'idle' }
        ),
      getTrackedOperation: (id) => {
        server.asked.push(id);
        const run = server.runs.get(id);
        // A dropped run is kept for 5 minutes after the 10 second drop, then the id answers 200 with no status.
        const forgotten =
          !run ||
          ((run.status === 'completed' || run.status === 'cancelled') &&
            ref.runner.now - run.endedAt >= 310000);
        // The REST answer omits null fields, so a run without an error has no `error` key.
        const body = forgotten
          ? { id, active: false, percentComplete: 100 }
          : {
              id,
              active: false,
              status: run.status,
              ...(run.nextOperationId ? { nextOperationId: run.nextOperationId } : {}),
              ...(run.status === 'failed' ? { error: 'Database unavailable' } : {})
            };
        return runGate ? runGate.then(() => body) : Promise.resolve(body);
      },
      forceKillOperation: (id) => {
        server.killed.push(id);
        if (server.runGone) {
          return Promise.reject(new ApiError(404, 'Operation not found or already completed'));
        }
        server.processing = false;
        const status = server.endingOnForceStop;
        server.runs.set(server.operationId, {
          status,
          endedAt: status === 'completed' || status === 'cancelled' ? ref.runner.now : 0
        });
        return Promise.resolve({ message: 'Operation force killed' });
      },
      resetLogPosition: () => Promise.resolve(),
      // The start endpoints answer the new pass's id (OperationResponse.operationId); a queued start answers the queued
      // run's id with `queued: true` (QueuedOperationResponse.cs), and the processing status still names the run in front.
      processAllLogs: () =>
        Promise.resolve(server.startAnswer ?? { operationId: server.operationId }),
      getLogPositions: () => Promise.resolve([])
    },
    getErrorMessage: (error) => error.message,
    formatCount: String,
    Button: 'Button',
    ProgressBar: 'ProgressBar',
    Tooltip: 'Tooltip',
    Badge: 'Badge',
    CollapsibleRegion: 'CollapsibleRegion',
    LoadingSpinner: 'LoadingSpinner',
    ConfirmationModal: 'ConfirmationModal',
    FileText: 'FileText',
    CheckCircle: 'CheckCircle',
    FolderOpen: 'FolderOpen',
    ChevronDown: 'ChevronDown',
    ChevronUp: 'ChevronUp',
    PlayCircle: 'PlayCircle',
    XCircle: 'XCircle'
  });
  ref.runner = runner;
  const flush = async () => {
    for (let i = 0; i < 5; i++) await settle();
    runner.render();
  };
  runner.render({ onComplete: () => undefined, onSkip: () => undefined });
  runner.flushPassive();
  await flush();
  return {
    server,
    view: () => {
      const found = elements(runner.tree);
      return {
        spinner: found.some((node) => node.type === 'LoadingSpinner'),
        continueButton: found.some(
          (node) => node.type === 'Button' && node.props.color === 'secondary'
        ),
        processAllButton: found.some(
          (node) =>
            node.type === 'Button' &&
            node.props.children.includes('initialization.logProcessing.processAllLogs')
        ),
        texts: found
          .filter((node) => node.type === 'p')
          .flatMap((node) => node.props.children)
          .filter((child) => typeof child === 'string')
      };
    },
    forceStop: async () => {
      runner.event(() =>
        elements(runner.tree)
          .find((node) => node.type === 'ConfirmationModal')
          .props.onConfirm()
      );
      await flush();
    },
    emit: async (event, completion) => {
      runner.event(() => handlers.get(event)(completion));
      await flush();
    },
    processAll: async () => {
      runner.event(() =>
        elements(runner.tree)
          .find(
            (node) =>
              node.type === 'Button' &&
              node.props.children.includes('initialization.logProcessing.processAllLogs')
          )
          .props.onClick()
      );
      await flush();
    },
    // Holds every run read until the returned function releases them, as a slow request would.
    holdRunReads: () => {
      let release;
      runGate = new Promise((resolve) => {
        release = resolve;
      });
      return async () => {
        runGate = null;
        release();
        await flush();
      };
    },
    // Time passes in the watchdog's own 5 second ticks, reading the run the way each tick would.
    advanceTo: async (seconds) => {
      while (runner.now < seconds * 1000) {
        runner.advance(Math.min(5000, seconds * 1000 - runner.now));
        await flush();
      }
    },
    dispose: () => runner.dispose()
  };
}

test('a force stop whose completion event is lost shows the canceled notice', async () => {
  const wizard = await startWizard({ endingOnForceStop: 'cancelled' });
  await wizard.forceStop();
  await wizard.advanceTo(40);
  const view = wizard.view();
  assert.ok(
    view.texts.includes('initialization.logProcessing.cancelled'),
    'the canceled run is read before it is dropped'
  );
  assert.equal(view.spinner, false);
  wizard.dispose();
});

test('a force stop after the pass saved shows the completed view', async () => {
  const wizard = await startWizard({ endingOnForceStop: 'completed' });
  await wizard.forceStop();
  await wizard.advanceTo(40);
  assert.equal(wizard.view().continueButton, true);
  wizard.dispose();
});

test('a pass still saving its outcome is read again until it ends', async () => {
  const wizard = await startWizard({ endingOnForceStop: 'running' });
  await wizard.forceStop();
  await wizard.advanceTo(7);
  wizard.server.runs.set('op', { status: 'completed', endedAt: 7000 });
  await wizard.advanceTo(40);
  assert.equal(wizard.view().continueButton, true);
  wizard.dispose();
});

test('a lost completion event of a failed pass shows the failure notice', async () => {
  const wizard = await startWizard({});
  wizard.server.processing = false;
  wizard.server.runs.set('op', { status: 'failed', endedAt: 0 });
  await wizard.advanceTo(40);
  assert.ok(wizard.view().texts.includes('initialization.logProcessing.failedToProcess'));
  wizard.dispose();
});

test('a cancel that finds the run already gone shows no error', async () => {
  const wizard = await startWizard({ runGone: true });
  wizard.server.processing = false;
  wizard.server.runs.set('op', { status: 'cancelled', endedAt: -60000 });
  await wizard.forceStop();
  assert.ok(
    !wizard.view().texts.includes('Operation not found or already completed'),
    'the run already ended, so there is nothing to report'
  );
  await wizard.advanceTo(10);
  assert.equal(wizard.view().spinner, false, 'the watchdog ends the step');
  wizard.dispose();
});

test('a lost completion event with no force stop shows Continue', async () => {
  const wizard = await startWizard({});
  await wizard.advanceTo(3);
  wizard.server.processing = false;
  wizard.server.runs.set('op', { status: 'completed', endedAt: 3000 });
  await wizard.advanceTo(45);
  assert.equal(wizard.view().continueButton, true);
  wizard.dispose();
});

test('a stale tick leaves a new pass running', async () => {
  const wizard = await startWizard({});
  const release = wizard.holdRunReads();
  wizard.server.processing = false;
  wizard.server.runs.set('op', { status: 'cancelled', endedAt: 0 });
  // The tick at 30 seconds reads the idle status, and its run read stays out.
  await wizard.advanceTo(30);
  await wizard.emit('LogProcessingComplete', {
    operationId: 'op',
    success: false,
    cancelled: true
  });
  wizard.server.processing = true;
  wizard.server.operationId = 'op2';
  await wizard.processAll();
  await release();
  const view = wizard.view();
  assert.equal(view.spinner, true, 'the new pass keeps its running view');
  assert.equal(view.processAllButton, false);
  assert.ok(!view.texts.includes('initialization.logProcessing.cancelled'));
  wizard.dispose();
});

test("a stale tick keeps the completion event's failure message", async () => {
  const wizard = await startWizard({});
  const release = wizard.holdRunReads();
  wizard.server.processing = false;
  wizard.server.runs.set('op', { status: 'failed', endedAt: 0 });
  await wizard.advanceTo(30);
  await wizard.emit('LogProcessingComplete', {
    operationId: 'op',
    success: false,
    message: 'Database unavailable'
  });
  await release();
  const view = wizard.view();
  assert.ok(view.texts.includes('Database unavailable'));
  assert.ok(!view.texts.includes('initialization.logProcessing.failedToProcess'));
  wizard.dispose();
});

test("another tab's pass does not replace the pass this step started", async () => {
  const wizard = await startWizard({});
  // This step's pass saved and its completion event was lost; another tab then started op2 and canceled it.
  wizard.server.runs.set('op', { status: 'completed', endedAt: 0 });
  wizard.server.runs.set('op2', { status: 'running', endedAt: 0 });
  wizard.server.operationId = 'op2';
  await wizard.advanceTo(30);
  wizard.server.processing = false;
  wizard.server.runs.set('op2', { status: 'cancelled', endedAt: 30000 });
  await wizard.advanceTo(70);
  const view = wizard.view();
  assert.deepEqual(wizard.server.asked, ['op'], 'only the pass this step started is read');
  assert.equal(view.continueButton, true);
  assert.ok(!view.texts.includes('initialization.logProcessing.cancelled'));
  wizard.dispose();
});

test("a pass that ended before the start answer returned is read by the start answer's id", async () => {
  const wizard = await startWizard({ processing: false });
  wizard.server.operationId = 'op2';
  wizard.server.runs.set('op2', { status: 'completed', endedAt: 0 });
  await wizard.processAll();
  await wizard.advanceTo(40);
  assert.deepEqual(wizard.server.asked, ['op2']);
  assert.equal(wizard.view().continueButton, true);
  wizard.dispose();
});

test("a pass queued behind another tab's pass keeps its running view", async () => {
  const wizard = await startWizard({});
  wizard.server.runs.set('op', { status: 'waiting', endedAt: 0 });
  wizard.server.runs.set('op2', { status: 'running', endedAt: 0 });
  wizard.server.operationId = 'op2';
  await wizard.advanceTo(40);
  const view = wizard.view();
  assert.equal(view.spinner, true);
  assert.equal(view.continueButton, false);
  wizard.dispose();
});

test('a queued pass is force-stopped by its own id', async () => {
  const wizard = await startWizard({ processing: false });
  wizard.server.processing = true;
  wizard.server.operationId = 'saving-a';
  wizard.server.startAnswer = {
    operationId: 'queued-b',
    queued: true,
    alreadyRunning: false,
    status: 'waiting'
  };
  await wizard.processAll();
  await wizard.forceStop();
  assert.deepEqual(wizard.server.killed, ['queued-b'], 'the run in front is left alone');
  wizard.dispose();
});

test('a pass that handed its work to another run follows that run', async () => {
  const wizard = await startWizard({});
  wizard.server.processing = false;
  wizard.server.runs.set('op', { status: 'completed', endedAt: 0, nextOperationId: 'op3' });
  wizard.server.runs.set('op3', { status: 'running', endedAt: 0 });
  await wizard.advanceTo(32);
  const view = wizard.view();
  assert.equal(view.spinner, true, 'the run that took over is still running');
  assert.equal(view.continueButton, false);
  wizard.dispose();
});

test('a promoted run that already ended is shown at the next tick', async () => {
  const wizard = await startWizard({});
  wizard.server.processing = false;
  wizard.server.runs.set('op', { status: 'completed', endedAt: 0, nextOperationId: 'op3' });
  wizard.server.runs.set('op3', { status: 'completed', endedAt: 0 });
  await wizard.advanceTo(32);
  await wizard.advanceTo(38);
  const view = wizard.view();
  assert.equal(view.continueButton, true, 'the run that took over already ended');
  assert.equal(view.spinner, false);
  wizard.dispose();
});

test("another tab's pass that starts after this step's pass completed keeps Continue", async () => {
  const wizard = await startWizard({});
  await wizard.emit('LogProcessingComplete', {
    operationId: 'op',
    success: true,
    cancelled: false,
    entriesProcessed: 5,
    linesProcessed: 5
  });
  assert.equal(wizard.view().continueButton, true);
  await wizard.emit('LogProcessingStarted', { operationId: 'other-tab-pass' });
  await wizard.emit('LogProcessingProgress', {
    operationId: 'other-tab-pass',
    percentComplete: 40,
    status: 'processing'
  });
  await wizard.emit('LogProcessingComplete', {
    operationId: 'other-tab-pass',
    success: false,
    cancelled: false,
    message: 'Other pass failed'
  });
  const view = wizard.view();
  assert.equal(view.continueButton, true, "the other tab's pass is not this step's pass");
  assert.ok(!view.texts.includes('Other pass failed'));
  wizard.dispose();
});

test('a queued pass the queue could not start says so', async () => {
  const wizard = await startWizard({});
  wizard.server.processing = false;
  wizard.server.runs.set('op', { status: 'skipped', endedAt: 0 });
  await wizard.advanceTo(40);
  const view = wizard.view();
  assert.ok(view.texts.includes('initialization.logProcessing.failedToProcessDatasource'));
  assert.equal(view.spinner, false);
  assert.equal(view.continueButton, false);
  wizard.dispose();
});
