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
// (UnifiedOperationTracker.cs) and keeps a failed one; GET /api/operations/{id} then answers 200 with no status
// (OperationsController.cs); POST /api/operations/{id}/force-kill answers 404 "Operation not found or already
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
    runStatus: 'running',
    endedAt: 0,
    runGone: false,
    endingOnForceStop: 'cancelled',
    ...overrides
  };
  const ref = {};
  const runner = mount('LogProcessingStep', {
    useTranslation: () => ({ t: (key) => key }),
    useSignalR: () => ({ on: () => undefined, off: () => undefined }),
    useConfig: () => ({ config: { dataSources: [] } }),
    useSelectionSet: () => ({ selected: new Set(), toggle: () => undefined }),
    ApiError,
    ApiService: {
      getProcessingStatus: () =>
        Promise.resolve(
          server.processing
            ? { isProcessing: true, operationId: 'op', progress: 40, status: 'processing' }
            : { isProcessing: false, operationId: null, status: 'idle' }
        ),
      getTrackedOperation: (id) => {
        const dropped =
          (server.runStatus === 'completed' || server.runStatus === 'cancelled') &&
          ref.runner.now - server.endedAt >= 10000;
        return Promise.resolve(
          dropped
            ? { id, active: false, percentComplete: 100 }
            : {
                id,
                active: false,
                status: server.runStatus,
                error: server.runStatus === 'failed' ? 'Database unavailable' : null
              }
        );
      },
      forceKillOperation: () => {
        if (server.runGone) {
          return Promise.reject(new ApiError(404, 'Operation not found or already completed'));
        }
        server.processing = false;
        server.runStatus = server.endingOnForceStop;
        if (server.runStatus === 'completed' || server.runStatus === 'cancelled') {
          server.endedAt = ref.runner.now;
        }
        return Promise.resolve({ message: 'Operation force killed' });
      },
      resetLogPosition: () => Promise.resolve(),
      processAllLogs: () => Promise.resolve(),
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
  wizard.server.runStatus = 'completed';
  wizard.server.endedAt = 7000;
  await wizard.advanceTo(40);
  assert.equal(wizard.view().continueButton, true);
  wizard.dispose();
});

test('a lost completion event of a failed pass shows the failure notice', async () => {
  const wizard = await startWizard({});
  wizard.server.processing = false;
  wizard.server.runStatus = 'failed';
  await wizard.advanceTo(40);
  assert.ok(wizard.view().texts.includes('initialization.logProcessing.failedToProcess'));
  wizard.dispose();
});

test('a cancel that finds the run already gone shows no error', async () => {
  const wizard = await startWizard({ runGone: true });
  wizard.server.processing = false;
  wizard.server.runStatus = 'cancelled';
  wizard.server.endedAt = -60000;
  await wizard.forceStop();
  assert.ok(
    !wizard.view().texts.includes('Operation not found or already completed'),
    'the run already ended, so there is nothing to report'
  );
  await wizard.advanceTo(10);
  assert.equal(wizard.view().spinner, false, 'the watchdog ends the step');
  wizard.dispose();
});
