import assert from 'node:assert/strict';
import test from 'node:test';
import ts from 'typescript';
import { bindLifted, findSoleNode, liftHookCallback, parseSource } from './transpile-module.mjs';

/**
 * Status Check reopened from its cached status. Run is clickable before the mount read answers,
 * so that read, sent before Run, can answer after Run's reply, after a sweep's completion, or after
 * the conflict read a 409 sends. The page has to end on the newest state, not on the old read.
 *
 * The ref's initial value, the completion handler, the Run handler and the mount read are all
 * lifted from the shipped file and share one set of refs, so the order each test drives is the one
 * the page meets.
 */

const SECTION = 'src/components/features/management/status-check/StatusCheckSection.tsx';
const KEYS = 'management.sections.statusCheck';

class ApiError extends Error {
  constructor(kind) {
    super(kind);
    this.kind = kind;
  }
}

/** A promise with its resolve function, for a reply the test answers when it chooses. */
const deferred = () => {
  let resolve;
  const promise = new Promise((done) => {
    resolve = done;
  });
  return { promise, resolve };
};

/**
 * Lifts the page's handlers around one cached status. `statusReads` answers each status read in
 * the order they are sent; `runStatusCheck` is the Run request.
 */
const openPage = ({ cached, statusReads, runStatusCheck }) => {
  const page = { runError: undefined, isRunning: false, status: cached, statusReadsSent: 0 };
  const source = parseSource(SECTION);
  const declaration = findSoleNode(
    source,
    'currentOperationRef declaration',
    (node) => ts.isVariableDeclaration(node) && node.name.getText(source) === 'currentOperationRef'
  );
  const currentOperationRef = bindLifted(`() => (${declaration.initializer.getText(source)})`, {
    useRef: (value) => ({ current: value }),
    getCachedStatus: () => cached
  })();
  const lastFinishedOperationRef = { current: null };
  const finishedOperationIdsRef = { current: new Set() };
  const statusVersionRef = { current: 0 };
  const handlers = new Map();
  const setRunError = (value) => {
    page.runError = value;
  };
  const setIsRunning = (value) => {
    page.isRunning = value;
  };
  const setStatus = (value) => {
    page.status = typeof value === 'function' ? value(page.status) : value;
  };

  bindLifted(liftHookCallback(SECTION, 'useEffect', 'handleComplete'), {
    on: (name, handler) => handlers.set(name, handler),
    off: () => undefined,
    lastFinishedOperationRef,
    finishedOperationIdsRef,
    currentOperationRef,
    statusVersionRef,
    setIsRunning,
    setProgress: () => undefined,
    setRunError,
    setStatus,
    reloadDomains: () => undefined
  })();

  const ApiService = {
    runStatusCheck: () => runStatusCheck(handlers),
    getStatusCheck: () => {
      const answer = statusReads[page.statusReadsSent];
      page.statusReadsSent += 1;
      return answer;
    },
    getCacheDomains: async () => ({ services: [] })
  };

  let loadAll;
  loadAll = bindLifted(liftHookCallback(SECTION, 'useCallback', 'getCacheDomains(signal)'), {
    mockMode: false,
    ApiService,
    finishedOperationIdsRef,
    currentOperationRef,
    statusVersionRef,
    loadAll: (signal) => loadAll(signal),
    setStatus,
    setIsRunning,
    setStatusError: () => undefined,
    setDomainGroups: () => undefined,
    setReloadError: () => undefined,
    setIsLoading: () => undefined,
    isAbortError: () => false,
    getErrorMessage: (error) => error.message
  });
  const handleRun = bindLifted(liftHookCallback(SECTION, 'useCallback', 'runStatusCheck'), {
    ApiService,
    ApiError,
    lastFinishedOperationRef,
    finishedOperationIdsRef,
    currentOperationRef,
    statusVersionRef,
    setRunError,
    setIsRunning,
    setProgress: () => undefined,
    setStatus,
    keys: KEYS
  });
  return { page, currentOperationRef, loadAll, handleRun };
};

const idle = (overrides = {}) => ({
  lastResult: { services: [] },
  domainsSource: null,
  isRunning: false,
  operationId: null,
  resolverMode: 'auto',
  ...overrides
});

test('a reopened page shows a new sweep that fails before its first progress', async () => {
  // The cache still names sweep x, which finished while the page was closed.
  const cached = idle({ operationId: 'x' });
  const failure = {
    operationId: 'y',
    success: false,
    error: 'boom',
    result: null,
    stageKey: 'signalr.statusCheck.failed',
    context: { errorDetail: 'boom' },
    canceled: false
  };
  // The mount read was sent before Run and answers last, with the old sweep's finished state.
  const mountRead = deferred();
  const { page, loadAll, handleRun } = openPage({
    cached,
    statusReads: [mountRead.promise, Promise.resolve(idle())],
    runStatusCheck: async (handlers) => {
      handlers.get('StatusCheckComplete')(failure);
      return { operationId: 'y' };
    }
  });

  const mounted = loadAll(new AbortController().signal);
  await handleRun();
  mountRead.resolve({ ...cached, operationId: null });
  await mounted;

  assert.deepEqual(page.runError, {
    stageKey: 'signalr.statusCheck.failed',
    context: { errorDetail: 'boom' }
  });
  assert.equal(page.isRunning, false);
});

test('a status read sent before Run does not mark the started sweep idle', async () => {
  const mountRead = deferred();
  const { page, currentOperationRef, loadAll, handleRun } = openPage({
    cached: idle(),
    statusReads: [mountRead.promise, Promise.resolve(idle({ isRunning: true, operationId: 'x' }))],
    runStatusCheck: async () => ({ operationId: 'x' })
  });

  const mounted = loadAll(new AbortController().signal);
  await handleRun();
  mountRead.resolve(idle());
  await mounted;

  assert.equal(page.isRunning, true);
  assert.equal(currentOperationRef.current, 'x');
});

test('a status read sent before Run does not put the previous result back', async () => {
  const previous = { services: [{ service: 'steam', status: 'ok' }] };
  const latest = { services: [{ service: 'steam', status: 'failing' }] };
  const mountRead = deferred();
  const { page, loadAll, handleRun } = openPage({
    cached: idle({ lastResult: previous }),
    statusReads: [mountRead.promise, Promise.resolve(idle({ lastResult: latest }))],
    runStatusCheck: async (handlers) => {
      // The sweep finishes before the Run reply is written.
      handlers.get('StatusCheckComplete')({
        operationId: 'x',
        success: true,
        result: latest,
        canceled: false
      });
      return { operationId: 'x' };
    }
  });

  const mounted = loadAll(new AbortController().signal);
  await handleRun();
  mountRead.resolve(idle({ lastResult: previous }));
  await mounted;

  assert.equal(page.status.lastResult, latest);
  assert.equal(page.isRunning, false);
});

test('a status read sent before a refused Run does not hide the sweep already running', async () => {
  const mountRead = deferred();
  const running = idle({ isRunning: true, operationId: 'y' });
  const { page, currentOperationRef, loadAll, handleRun } = openPage({
    cached: idle(),
    statusReads: [mountRead.promise, Promise.resolve(running), Promise.resolve(running)],
    runStatusCheck: async () => {
      throw new ApiError('conflict');
    }
  });

  const mounted = loadAll(new AbortController().signal);
  await handleRun();
  mountRead.resolve(idle());
  await mounted;

  assert.equal(page.isRunning, true);
  assert.equal(currentOperationRef.current, 'y');
});

test('a status read dropped for being older is read again, so the server resolver mode shows', async () => {
  // Another browser changed the mode while this page was closed; the cache still has the old one.
  const mountRead = deferred();
  const { page, loadAll, handleRun } = openPage({
    cached: idle({ resolverMode: 'auto' }),
    statusReads: [
      mountRead.promise,
      Promise.resolve(idle({ resolverMode: 'host', isRunning: true, operationId: 'x' }))
    ],
    runStatusCheck: async () => ({ operationId: 'x' })
  });

  const mounted = loadAll(new AbortController().signal);
  await handleRun();
  mountRead.resolve(idle({ resolverMode: 'host' }));
  await mounted;

  assert.equal(page.statusReadsSent, 2);
  assert.equal(page.status.resolverMode, 'host');
  assert.equal(page.isRunning, true);
});
