import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import { bindLifted, findSoleNode, liftHookCallback, parseSource } from './transpile-module.mjs';

/**
 * The comparison chart reads /api/dashboard/event-compare from an effect that re-runs only on a
 * selection, metric or mock-mode change, so nothing in the component noticed a compared event being
 * renamed or deleted, or a running event's totals moving. The rest of the dashboard had already
 * repainted off the same feed while this chart still drew the old figures.
 *
 * The subscription is written inside the component and never exported, so these lift the arrows that
 * ship out of the source and run them, rather than a copy written here. [20]
 */

const COMPONENT_PATH = 'src/components/features/dashboard/widgets/EventCompareChart.tsx';

const parseComponent = () => parseSource(COMPONENT_PATH, typescript.ScriptKind.TSX);

const eventListDeclaration = () => {
  const sourceFile = parseComponent();
  const declaration = findSoleNode(
    sourceFile,
    'EVENT_COMPARE_EVENTS declaration',
    (node) =>
      typescript.isVariableDeclaration(node) &&
      node.name.getText(sourceFile) === 'EVENT_COMPARE_EVENTS'
  );
  return { sourceFile, declaration };
};

const eventNames = () => {
  const { sourceFile, declaration } = eventListDeclaration();
  return declaration.initializer.elements.map((element) =>
    element.getText(sourceFile).replaceAll("'", '')
  );
};

/** Runs the subscription effect against a recording hub and returns everything it did. */
const runSubscription = () => {
  const subscribed = [];
  const unsubscribed = [];
  const scheduled = [];
  const reloads = [];
  const cleanup = bindLifted(
    liftHookCallback(COMPONENT_PATH, 'useEffect', 'EVENT_COMPARE_EVENTS'),
    {
      EVENT_COMPARE_EVENTS: eventNames(),
      on: (name, handler) => subscribed.push({ name, handler }),
      off: (name, handler) => unsubscribed.push({ name, handler }),
      scheduleReload: (fn) => scheduled.push(fn),
      reload: () => reloads.push(true)
    }
  )();
  return { subscribed, unsubscribed, scheduled, reloads, cleanup };
};

test('the chart names both the events that redefine a comparison and the ones that move it', () => {
  assert.deepEqual(eventNames().slice().sort(), [
    'DownloadsRefresh',
    'EventCreated',
    'EventDeleted',
    'EventUpdated',
    'EventsCleared',
    'LogProcessingComplete'
  ]);
});

test('the event list is checked against the event union, which on and off do not check', () => {
  const { sourceFile, declaration } = eventListDeclaration();
  assert.equal(
    declaration.type?.getText(sourceFile),
    'readonly SignalREventName[]',
    'on and off take a plain string, so this annotation is the only thing catching a stale name'
  );
});

test('every named event schedules the reload instead of fetching straight away', () => {
  const { subscribed, scheduled, reloads } = runSubscription();

  assert.equal(subscribed.length, 6, 'one subscription per name');
  for (const { handler } of subscribed) {
    handler();
  }

  assert.equal(reloads.length, 0, 'a burst has to collapse into one fetch, not six');
  assert.equal(scheduled.length, 6, 'each arrival goes through the shared debounce');

  scheduled.at(-1)();
  assert.equal(reloads.length, 1);
});

test('unmounting removes every subscription with the handler it added', () => {
  const { subscribed, unsubscribed, cleanup } = runSubscription();

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

test('a reload reaches the endpoint rather than only re-rendering', () => {
  let version = 3;
  bindLifted(liftHookCallback(COMPONENT_PATH, 'useCallback', 'setRefreshVersion'), {
    setRefreshVersion: (next) => {
      version = next(version);
    }
  })();
  assert.equal(version, 4, 'the bump is what makes the effect ask again');

  const sourceFile = parseComponent();
  const fetchEffect = findSoleNode(
    sourceFile,
    'event-compare fetch effect',
    (node) =>
      typescript.isCallExpression(node) &&
      node.expression.getText(sourceFile) === 'useEffect' &&
      node.arguments[0].getText(sourceFile).includes('ApiService.getEventCompare')
  );
  const deps = fetchEffect.arguments[1].elements.map((element) => element.getText(sourceFile));

  assert.ok(
    deps.includes('refreshVersion'),
    'without the version among its deps the bump re-renders and never refetches'
  );
});

test('the chart asks again once the socket is back', () => {
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
    'reload',
    'events raised while the socket was down are never delivered, so recovery has to refetch'
  );
});
