import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import { bindLifted, findSoleNode, liftHookCallback, parseSource } from './transpile-module.mjs';

/**
 * The excluded-client panel read its rules once, on mount. One admin saving the list left every
 * other admin's panel showing the old rules until they reloaded the page, and the save they made
 * next wrote those old rules back over the new ones.
 *
 * The panel also holds unsaved edits, so the reload has two windows in which the user can start
 * typing after it was decided on: the debounce, and the request itself. Losing what they typed is
 * worse than the staleness, so both are checked here against the arrows the component ships.
 */

const COMPONENT_PATH = 'src/components/features/management/sections/ClientsSection.tsx';

const parseComponent = () => parseSource(COMPONENT_PATH, typescript.ScriptKind.TSX);

const eventListDeclaration = () => {
  const sourceFile = parseComponent();
  const declaration = findSoleNode(
    sourceFile,
    'CLIENT_EXCLUSION_EVENTS declaration',
    (node) =>
      typescript.isVariableDeclaration(node) &&
      node.name.getText(sourceFile) === 'CLIENT_EXCLUSION_EVENTS'
  );
  return { sourceFile, declaration };
};

const eventNames = () => {
  const { sourceFile, declaration } = eventListDeclaration();
  return declaration.initializer.elements.map((element) =>
    element.getText(sourceFile).replaceAll("'", '')
  );
};

/**
 * Delivers the event through the component's own subscription effect and its own reload callback,
 * with the debounce fired by hand. `editing` is what the ref holds when the timer elapses, which is
 * the moment a user who started typing during the wait has to be noticed.
 */
const deliverEvent = ({ editing }) => {
  const loads = [];
  const subscribed = [];
  const unsubscribed = [];
  const scheduled = [];

  const reloadExclusions = bindLifted(
    liftHookCallback(COMPONENT_PATH, 'useCallback', 'loadExcludedIps(false)'),
    {
      hasExcludedChangesRef: { current: editing },
      loadExcludedIps: (showLoading) => {
        loads.push(showLoading);
        return Promise.resolve();
      }
    }
  );

  const cleanup = bindLifted(
    liftHookCallback(COMPONENT_PATH, 'useEffect', 'CLIENT_EXCLUSION_EVENTS'),
    {
      CLIENT_EXCLUSION_EVENTS: eventNames(),
      on: (name, handler) => subscribed.push({ name, handler }),
      off: (name, handler) => unsubscribed.push({ name, handler }),
      scheduleReload: (fn) => scheduled.push(fn),
      reloadExclusions
    }
  )();

  for (const { handler } of subscribed) {
    handler();
  }
  for (const fire of scheduled) {
    fire();
  }

  return { loads, subscribed, unsubscribed, scheduled, cleanup };
};

test('the panel answers the event a saved exclusion change raises', () => {
  assert.deepEqual(eventNames(), ['DownloadsRefresh']);
});

test('the event list is checked against the event union, which on and off do not check', () => {
  const { sourceFile, declaration } = eventListDeclaration();
  assert.equal(
    declaration.type?.getText(sourceFile),
    'readonly SignalREventName[]',
    'on and off take a plain string, so this annotation is the only thing catching a stale name'
  );
});

test("another admin's save reaches this panel without a reload", () => {
  const { loads, scheduled } = deliverEvent({ editing: false });

  assert.equal(scheduled.length, 1, 'the arrival goes through the shared debounce');
  assert.deepEqual(
    loads,
    [false],
    'a background reload must not blank the list or disable the controls the user is holding'
  );
});

test('a draft the user is still typing is never replaced by the reload', () => {
  const { loads, scheduled } = deliverEvent({ editing: true });

  assert.equal(scheduled.length, 1, 'the event still arrives; only the reload is declined');
  assert.deepEqual(loads, [], 'refetching here would replace the rules the user is editing');
});

test('a reply that lands after the user starts typing is dropped', async () => {
  const typed = [{ ip: '10.0.0.9', mode: 'exclude' }];
  const editing = { current: false };
  let shown = typed;
  let saved = [];

  const loadExcludedIps = bindLifted(
    liftHookCallback(COMPONENT_PATH, 'useCallback', 'ApiService.getStatsExclusions'),
    {
      isAdmin: true,
      mockMode: false,
      hasExcludedChangesRef: editing,
      setLoadingExcluded: () => undefined,
      ApiService: {
        getStatsExclusions: async () => {
          // The user types while the request is in flight, which the check before it cannot see.
          editing.current = true;
          return { rules: [{ ip: '10.0.0.1', mode: 'exclude' }] };
        }
      },
      setExcludedRules: (rules) => {
        shown = rules;
      },
      setSavedExcludedRules: (rules) => {
        saved = rules;
      },
      onError: () => undefined,
      getErrorMessage: () => '',
      t: (key) => key
    }
  );

  await loadExcludedIps(false);

  assert.deepEqual(shown, typed, 'the reply is a request old and the draft is what is on screen');
  assert.deepEqual(saved, [], 'writing the saved copy alone would silently arm the next save');
});

test('unmounting removes every subscription with the handler it added', () => {
  const { subscribed, unsubscribed, cleanup } = deliverEvent({ editing: false });

  cleanup();

  assert.deepEqual(
    unsubscribed.map((entry) => entry.name),
    subscribed.map((entry) => entry.name)
  );
  for (const [index, entry] of subscribed.entries()) {
    assert.equal(
      unsubscribed[index].handler,
      entry.handler,
      'off has to be given the same handler reference on was given, or the subscription leaks'
    );
  }
});

test('the panel asks again once the socket is back', () => {
  const sourceFile = parseComponent();
  const call = findSoleNode(
    sourceFile,
    'reconnect refetch',
    (node) =>
      typescript.isCallExpression(node) &&
      node.expression.getText(sourceFile) === 'useReconnectRefetch'
  );

  assert.equal(call.arguments[0].getText(sourceFile), 'isConnected');
  assert.equal(
    call.arguments[1].getText(sourceFile),
    'reloadExclusions',
    'a save made while the socket was down raises an event this panel never receives'
  );
});
