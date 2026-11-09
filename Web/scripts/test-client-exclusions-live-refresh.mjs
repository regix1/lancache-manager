import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import typescript from 'typescript';
import {
  bindLifted,
  collectNodes,
  declaredValue,
  findSoleNode,
  liftConstArrow,
  liftHookCallback,
  parseSource
} from './transpile-module.mjs';

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

const rule = (ip, overrides = {}) => ({
  ip,
  mode: 'exclude',
  traffic: 'all',
  services: [],
  ...overrides
});

const withoutRowIds = (rows) =>
  rows.map((row) => ({ ip: row.ip, mode: row.mode, traffic: row.traffic, services: row.services }));

const settle = () => new Promise((resolve) => setImmediate(resolve));

const arrowDeclarations = (sourceFile, name) =>
  collectNodes(
    sourceFile,
    (node) =>
      typescript.isVariableDeclaration(node) &&
      node.name.getText(sourceFile) === name &&
      node.initializer !== undefined &&
      typescript.isArrowFunction(node.initializer)
  );

// The list helpers other arrows read as free names. Undefined when the file has none, so a case
// still runs far enough to show which behavior is missing rather than stopping at the lift.
const optionalArrow = (sourceFile, name, bindings) =>
  arrowDeclarations(sourceFile, name).length === 1
    ? bindLifted(liftConstArrow(COMPONENT_PATH, name), bindings)
    : undefined;

const listHelpers = () => {
  const sourceFile = parseComponent();
  const ruleKey = optionalArrow(sourceFile, 'ruleKey', {});
  return { ruleKey, assignRowIds: optionalArrow(sourceFile, 'assignRowIds', { ruleKey }) };
};

// What every read of the list needs beside its own fakes: the row numbering and the save guards.
const listBindings = () => ({
  ...listHelpers(),
  excludedRulesRef: { current: [] },
  nextRowIdRef: { current: 0 },
  savingExcludedRef: { current: false },
  savesStartedRef: { current: 0 }
});

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
      savingExcludedRef: { current: false },
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
  const typed = [{ ...rule('10.0.0.9'), rowId: 1 }];
  const editing = { current: false };
  let shown = typed;
  let saved = [];
  const loadErrors = [];

  const loadExcludedIps = bindLifted(
    liftHookCallback(COMPONENT_PATH, 'useCallback', 'ApiService.getStatsExclusions'),
    {
      ...listBindings(),
      isAdmin: true,
      mockMode: false,
      hasExcludedChangesRef: editing,
      excludedRequestRef: { current: 0 },
      setLoadingExcluded: () => undefined,
      ApiService: {
        getStatsExclusions: async () => {
          // The user types while the request is in flight, which the check before it cannot see.
          editing.current = true;
          return { rules: [rule('10.0.0.1')] };
        }
      },
      setExcludedRules: (rules) => {
        shown = rules;
      },
      setSavedExcludedRules: (rules) => {
        saved = rules;
      },
      setExcludedLoadError: (value) => loadErrors.push(value),
      getErrorMessage: () => '',
      t: (key) => key
    }
  );

  await loadExcludedIps(false);

  assert.deepEqual(shown, typed, 'the reply is a request old and the draft is what is on screen');
  assert.deepEqual(saved, [], 'writing the saved copy alone would silently arm the next save');
  assert.deepEqual(loadErrors, [null], 'the read succeeded, so an earlier load error is cleared');
});

test('a failed read keeps the reason for the section box and raises no popup', async () => {
  const loadErrors = [];
  const popups = [];
  const shown = [];

  const loadExcludedIps = bindLifted(
    liftHookCallback(COMPONENT_PATH, 'useCallback', 'ApiService.getStatsExclusions'),
    {
      ...listBindings(),
      isAdmin: true,
      mockMode: false,
      hasExcludedChangesRef: { current: false },
      excludedRequestRef: { current: 0 },
      setLoadingExcluded: () => undefined,
      ApiService: {
        getStatsExclusions: async () => {
          throw new Error('server down');
        }
      },
      setExcludedRules: (rules) => shown.push(rules),
      setSavedExcludedRules: () => undefined,
      setExcludedLoadError: (value) => loadErrors.push(value),
      onError: (message) => popups.push(message),
      getErrorMessage: (error) => `reason: ${error.message}`,
      t: (key) => key
    }
  );

  await loadExcludedIps(true);

  assert.deepEqual(loadErrors, ['reason: server down']);
  assert.deepEqual(popups, [], 'a failed load shows the section box, never a popup');
  assert.deepEqual(shown, [], 'a failed load leaves the last rules on screen');
});

test('an older exclusions read that fails after a newer one succeeded shows no error', async () => {
  const loadErrors = [];
  const shown = [];
  const reads = [];

  const loadExcludedIps = bindLifted(
    liftHookCallback(COMPONENT_PATH, 'useCallback', 'ApiService.getStatsExclusions'),
    {
      ...listBindings(),
      isAdmin: true,
      mockMode: false,
      hasExcludedChangesRef: { current: false },
      excludedRequestRef: { current: 0 },
      setLoadingExcluded: () => undefined,
      ApiService: {
        getStatsExclusions: () =>
          new Promise((resolve, reject) => {
            reads.push({ resolve, reject });
          })
      },
      setExcludedRules: (rules) => shown.push(rules),
      setSavedExcludedRules: () => undefined,
      setExcludedLoadError: (value) => loadErrors.push(value),
      getErrorMessage: (error) => `reason: ${error.message}`
    }
  );

  const older = loadExcludedIps(false);
  const newer = loadExcludedIps(false);
  reads[1].resolve({ rules: [rule('10.0.0.1')] });
  await newer;
  reads[0].reject(new Error('stale read failed'));
  await older;

  assert.deepEqual(loadErrors, [null], 'the stale failure never reaches the section');
  assert.deepEqual(shown, [[{ ...rule('10.0.0.1'), rowId: 1 }]]);
});

test('a superseded first read that fails leaves the list loading until the newer read answers', async () => {
  const loading = [];
  const loadErrors = [];
  const shown = [];
  const reads = [];

  const loadExcludedIps = bindLifted(
    liftHookCallback(COMPONENT_PATH, 'useCallback', 'ApiService.getStatsExclusions'),
    {
      ...listBindings(),
      isAdmin: true,
      mockMode: false,
      hasExcludedChangesRef: { current: false },
      excludedRequestRef: { current: 0 },
      setLoadingExcluded: (value) => loading.push(value),
      ApiService: {
        getStatsExclusions: () =>
          new Promise((resolve, reject) => {
            reads.push({ resolve, reject });
          })
      },
      setExcludedRules: (rules) => shown.push(rules),
      setSavedExcludedRules: () => undefined,
      setExcludedLoadError: (value) => loadErrors.push(value),
      getErrorMessage: (error) => `reason: ${error.message}`
    }
  );

  // The mount read shows the skeleton; the reconnect read behind it does not.
  const mount = loadExcludedIps(true);
  const reconnect = loadExcludedIps(false);
  reads[0].reject(new Error('mount read failed'));
  await mount;

  // Loading ending here would show "No excluded IPs" and enable the editing controls over rules
  // nobody has read yet.
  assert.deepEqual(loading, [true]);
  assert.deepEqual(loadErrors, []);

  reads[1].resolve({ rules: [rule('10.0.0.1')] });
  await reconnect;

  assert.deepEqual(loading, [true, false], 'the newer read ends the loading the mount read began');
  assert.deepEqual(shown, [[{ ...rule('10.0.0.1'), rowId: 1 }]]);
});

// Renders the Client Hostnames body the admin sees, from the branch the component ships, and
// returns every element in it. The shared components are stand-ins so each can be counted.
const renderHostnamesBody = (hostnamesError) => {
  const sourceFile = parseComponent();
  const body = findSoleNode(
    sourceFile,
    'hostnames panel body',
    (node) =>
      typescript.isConditionalExpression(node) &&
      node.condition.getText(sourceFile) === '!isAdmin' &&
      node.getText(sourceFile).includes('HOSTNAME_SWITCHES')
  );
  const switches = findSoleNode(
    sourceFile,
    'HOSTNAME_SWITCHES declaration',
    (node) =>
      typescript.isVariableDeclaration(node) &&
      node.name.getText(sourceFile) === 'HOSTNAME_SWITCHES'
  );
  const components = {
    ErrorBlock: function ErrorBlock() {
      return null;
    },
    SettingRow: function SettingRow() {
      return null;
    },
    Alert: function Alert() {
      return null;
    },
    Button: function Button() {
      return null;
    }
  };
  const render = bindLifted(
    `() => (${body.getText(sourceFile)})`,
    {
      React,
      ...components,
      HOSTNAME_SWITCHES: bindLifted(`() => (${switches.initializer.getText(sourceFile)})`, {})(),
      isAdmin: true,
      hostnamesError,
      t: (key) => key,
      refreshHostnames: async () => undefined,
      visibleHostnamesReasonKey: null,
      hostnamesReason: 'none',
      dismissSomeUnnamed: () => undefined,
      hostnameForm: {
        enabled: false,
        guestAccess: false,
        routerLookup: true,
        dockerLookup: true,
        resolver: ''
      },
      setHostnameForm: () => undefined,
      savingHostnames: false,
      noAutofill: {},
      hostnamesChanged: false,
      handleHostnamesSave: async () => undefined,
      hostnamesLoading: false
    },
    { jsx: typescript.JsxEmit.React }
  );

  const elements = [];
  const visit = (node) => {
    if (Array.isArray(node)) {
      node.forEach(visit);
      return;
    }
    if (!React.isValidElement(node)) return;
    elements.push(node);
    visit(node.props.children);
  };
  visit(render());
  return { elements, components };
};

test('a failed hostnames read shows the box in place of the switches', () => {
  const { elements, components } = renderHostnamesBody('reason: server down');

  const boxes = elements.filter((element) => element.type === components.ErrorBlock);
  assert.equal(boxes.length, 1);
  assert.equal(boxes[0].props.title, 'management.sections.clients.hostnames.loadFailed');
  assert.equal(boxes[0].props.message, 'reason: server down');
  assert.equal(boxes[0].props.retryLabel, 'common.retry');
  assert.equal(
    elements.filter((element) => element.type === components.SettingRow).length,
    0,
    'the switches hold default values until a read succeeds, so none may show under the box'
  );
  assert.equal(
    elements.filter((element) => element.type === components.Button).length,
    0,
    'Save would write those default values back to the server'
  );
});

test('the switches come back once a hostnames read succeeds', () => {
  const { elements, components } = renderHostnamesBody(null);

  assert.equal(elements.filter((element) => element.type === components.ErrorBlock).length, 0);
  // The four hostname switches; prefill traffic is chosen per exclusion rule instead.
  assert.equal(elements.filter((element) => element.type === components.SettingRow).length, 4);
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

test('the address picker frame hides with its failure box under the connection banner', () => {
  const modal = parseSource(
    'src/components/modals/ClientGroupModal.tsx',
    typescript.ScriptKind.TSX
  );
  const failure = findSoleNode(
    modal,
    'address picker failure state',
    (node) => typescript.isIfStatement(node) && node.expression.getText(modal) === 'groupsError'
  );
  const failureBody = bindLifted(
    `() => (${failure.thenStatement.statements[0].expression.getText(modal)})`,
    {
      React,
      // ErrorBlock renders nothing while the connection banner is up.
      ErrorBlock: function ErrorBlock() {
        return null;
      },
      groupsError: 'reason: server down',
      t: (key) => key,
      refreshGroups: async () => undefined
    },
    { jsx: typescript.JsxEmit.React }
  );
  const frame = findSoleNode(
    modal,
    'address picker frame',
    (node) =>
      typescript.isJsxElement(node) &&
      node.openingElement.getText(modal).includes('clientgroup-ip-picker"')
  );
  const markup = bindLifted(
    `() => (${frame.getText(modal)})`,
    { React, renderPickerBody: failureBody },
    { jsx: typescript.JsxEmit.React }
  )();

  // Outage with no addresses: the frame holds only the empty failure wrapper.
  assert.equal(
    renderToStaticMarkup(markup),
    '<div class="mgmt-list divided-list clientgroup-ip-picker">' +
      '<div class="clientgroup-ip-picker__state"></div></div>',
    'the wrapper has to be truly empty for :empty to match'
  );
  const css = readFileSync(
    new URL('../src/components/modals/ClientGroupModal.css', import.meta.url),
    'utf8'
  );
  // Without this the frame's border stays as a 2px line under the banner.
  assert.match(
    css,
    /\.clientgroup-ip-picker:has\(>\s*\.clientgroup-ip-picker__state:empty\)\s*\{\s*display:\s*none;\s*\}/
  );
});

const prefillServices = () =>
  declaredValue(
    'src/components/features/prefill/persistentPrefillConstants.ts',
    'PERSISTENT_PREFILL_SERVICES'
  );

/**
 * The exclusions card's list, driven through the arrows the component ships. State writes land at
 * once and the refs read that state, the way they read it after React's next render. Every read and
 * every save waits until the case answers it, so a case can hold one while the user keeps editing.
 */
const exclusionsCard = () => {
  const { ruleKey, assignRowIds } = listHelpers();
  const serializeRules = bindLifted(liftHookCallback(COMPONENT_PATH, 'useCallback', ".join('|')"), {
    ruleKey
  });
  const state = { draft: [], saved: [], saving: false, loading: false, loadError: null };
  const setter = (key) => (value) => {
    state[key] = typeof value === 'function' ? value(state[key]) : value;
  };
  const reads = [];
  const saves = [];
  const sent = [];
  const notices = [];
  const ApiService = {
    getStatsExclusions: () =>
      new Promise((resolve, reject) => {
        reads.push({ resolve, reject });
      }),
    updateStatsExclusionRules: (rules) => {
      sent.push(rules);
      return new Promise((resolve, reject) => {
        saves.push({ resolve, reject });
      });
    }
  };
  const unsaved = () => serializeRules(state.draft) !== serializeRules(state.saved);
  const shared = {
    isAdmin: true,
    mockMode: false,
    ApiService,
    ruleKey,
    assignRowIds,
    serializeRules,
    excludedRequestRef: { current: 0 },
    nextRowIdRef: { current: 0 },
    savingExcludedRef: { current: false },
    savesStartedRef: { current: 0 },
    excludedRulesRef: {
      get current() {
        return state.draft;
      }
    },
    hasExcludedChangesRef: {
      get current() {
        return unsaved();
      }
    },
    setExcludedRules: setter('draft'),
    setSavedExcludedRules: setter('saved'),
    setSavingExcluded: setter('saving'),
    setLoadingExcluded: setter('loading'),
    setExcludedLoadError: setter('loadError'),
    getErrorMessage: (error) => error.message,
    t: (key) => key,
    onSuccess: (message) => notices.push(message),
    onError: (message) => notices.push(message),
    refreshStats: async () => undefined,
    refreshDownloads: async () => undefined,
    DEFAULT_EXCLUSION_MODE: 'exclude',
    PERSISTENT_PREFILL_SERVICES: prefillServices()
  };
  shared.loadExcludedIps = bindLifted(
    liftHookCallback(COMPONENT_PATH, 'useCallback', 'ApiService.getStatsExclusions'),
    shared
  );
  shared.reloadExclusions = bindLifted(
    liftHookCallback(COMPONENT_PATH, 'useCallback', 'loadExcludedIps(false)'),
    shared
  );
  shared.addRules = bindLifted(
    liftHookCallback(COMPONENT_PATH, 'useCallback', 'DEFAULT_EXCLUSION_MODE'),
    shared
  );
  // A handler closes over the render it came from, so each call sees the draft on screen now.
  const handler =
    (name, extra = {}) =>
    (...args) =>
      bindLifted(liftConstArrow(COMPONENT_PATH, name), {
        ...shared,
        excludedRules: state.draft,
        ...extra
      })(...args);

  return {
    state,
    reads,
    saves,
    sent,
    unsaved,
    ids: () => state.draft.map((row) => row.rowId),
    loadExcludedIps: shared.loadExcludedIps,
    reloadExclusions: shared.reloadExclusions,
    addRules: shared.addRules,
    changeMode: handler('handleChangeMode'),
    changeTraffic: handler('handleChangeTraffic'),
    changeServices: handler('handleChangeServices'),
    remove: handler('handleRemoveExcluded'),
    save: handler('handleSaveExcluded'),
    handler,
    load: async (rules) => {
      const loading = shared.loadExcludedIps(true);
      reads.at(-1).resolve({ ips: [], rules });
      await loading;
    },
    answerLatestRead: async (rules) => {
      reads.at(-1).resolve({ ips: [], rules });
      await settle();
    }
  };
};

test('a server answer keeps the row numbers of the rules it repeats', () => {
  const { ruleKey, assignRowIds } = listHelpers();
  assert.equal(
    ruleKey(rule('', { mode: 'hide', traffic: 'prefill', services: ['Xbox', 'Epic'] })),
    'hide|prefill||Epic,Xbox',
    'the same key the server builds'
  );
  const nextRowIdRef = { current: 2 };
  const current = [
    { ...rule('10.0.0.1'), rowId: 1 },
    { ...rule('10.0.0.2'), rowId: 2 }
  ];

  assert.deepEqual(
    assignRowIds([rule('10.0.0.1'), rule('10.0.0.2'), rule('10.0.0.3')], current, nextRowIdRef).map(
      (row) => row.rowId
    ),
    [1, 2, 3]
  );
  assert.deepEqual(
    assignRowIds([rule('10.0.0.1'), rule('10.0.0.1')], current, nextRowIdRef).map(
      (row) => row.rowId
    ),
    [1, 4],
    'one row number never goes to two rows'
  );
});

test('two rules for one address change and go one at a time', async () => {
  const card = exclusionsCard();
  await card.load([rule('10.0.0.5'), rule('10.0.0.5', { traffic: 'prefill' })]);
  const [allRow, prefillRow] = card.state.draft;

  card.changeMode(allRow.rowId, 'hide');
  assert.deepEqual(
    card.state.draft.map((row) => `${row.traffic}:${row.mode}`),
    ['all:hide', 'prefill:exclude'],
    'only the rule the user changed moves'
  );

  card.remove(prefillRow.rowId);
  assert.deepEqual(withoutRowIds(card.state.draft), [rule('10.0.0.5', { mode: 'hide' })]);
});

test('picking services on a prefill row keeps the row, so its open picker stays open', async () => {
  const card = exclusionsCard();
  await card.load([rule('', { traffic: 'prefill', mode: 'hide' })]);
  const [row] = card.state.draft;
  assert.equal(typeof row.rowId, 'number', 'the row is keyed by its own number, not its content');

  card.changeServices(row.rowId, ['Steam']);
  card.changeServices(row.rowId, ['Riot', 'Steam']);

  assert.equal(card.state.draft[0].rowId, row.rowId, 'a new key would remount the row');
  assert.deepEqual(card.state.draft[0].services, ['Steam', 'Riot'], 'kept in the service order');
});

test('traffic, services and a new prefill rule wait for Save', async () => {
  const card = exclusionsCard();
  await card.load([rule('10.0.0.5')]);
  const [row] = card.state.draft;

  card.changeTraffic(row.rowId, 'prefill');
  card.changeServices(row.rowId, ['Xbox']);
  card.addRules([''], 'prefill');

  assert.equal(card.sent.length, 0, 'nothing is written until Save');
  assert.equal(card.unsaved(), true);

  const saving = card.save();
  assert.deepEqual(card.sent, [
    [rule('10.0.0.5', { traffic: 'prefill', services: ['Xbox'] }), rule('', { traffic: 'prefill' })]
  ]);
  card.saves[0].resolve({ ips: [], rules: card.sent[0] });
  await saving;
  assert.equal(card.unsaved(), false);
});

test('switching a rule back to all traffic drops its services', async () => {
  const card = exclusionsCard();
  await card.load([rule('10.0.0.5', { traffic: 'prefill', services: ['Steam'] })]);
  const [row] = card.state.draft;

  card.changeTraffic(row.rowId, 'all');

  assert.deepEqual(withoutRowIds(card.state.draft), [rule('10.0.0.5')]);
});

test('a second click while the exclusions save runs sends nothing', async () => {
  const card = exclusionsCard();
  await card.load([rule('10.0.0.5')]);
  card.changeMode(card.state.draft[0].rowId, 'hide');

  const first = card.save();
  const second = card.save();
  assert.equal(card.sent.length, 1);
  assert.equal(card.state.saving, true, 'the Save button shows its progress');

  card.saves[0].resolve({ ips: [], rules: card.sent[0] });
  await first;
  await second;
  assert.equal(card.state.saving, false);
});

test('only the Save buttons read the saving flags, so every field stays usable', () => {
  const sourceFile = parseComponent();
  for (const [flag, handler] of [
    ['savingExcluded', 'handleSaveExcluded'],
    ['savingHostnames', 'handleHostnamesSave']
  ]) {
    const owners = collectNodes(
      sourceFile,
      (node) =>
        typescript.isJsxAttribute(node) &&
        node.initializer !== undefined &&
        new RegExp(`\\b${flag}\\b`).test(node.initializer.getText(sourceFile))
    ).map((attribute) => attribute.parent.parent.getText(sourceFile));

    assert.ok(owners.length > 0, `${flag} still drives its Save button`);
    for (const owner of owners) {
      assert.ok(owner.includes(handler), `${flag} disables something other than Save: ${owner}`);
    }
  }
});

test('a second click while the hostnames save runs sends nothing', async () => {
  const pending = [];
  const busy = [];
  const save = bindLifted(liftHookCallback(COMPONENT_PATH, 'useCallback', 'setHostnameSettings('), {
    savingHostnamesRef: { current: false },
    setSavingHostnames: (value) => busy.push(value),
    setHostnameSettings: () =>
      new Promise((resolve) => {
        pending.push(resolve);
      }),
    setHostnamesEnabled: async () => undefined,
    hostnameForm: {
      enabled: false,
      guestAccess: true,
      routerLookup: true,
      dockerLookup: true,
      resolver: ''
    },
    hostnamesEnabled: false,
    onSuccess: () => undefined,
    onError: () => undefined,
    t: (key) => key
  });

  const first = save();
  const second = save();
  assert.equal(pending.length, 1);
  pending[0]();
  await first;
  await second;
  assert.deepEqual(busy, [true, false]);
});

// The form's own first state, and the effect that copies the stored settings into it.
const hostnameReset = () => {
  const sourceFile = parseComponent();
  const formState = findSoleNode(
    sourceFile,
    'hostname form state',
    (node) =>
      typescript.isCallExpression(node) &&
      node.expression.getText(sourceFile) === 'useState' &&
      node.typeArguments?.[0]?.getText(sourceFile) === 'HostnameForm'
  );
  const initialForm = bindLifted(`() => (${formState.arguments[0].getText(sourceFile)})`, {})();
  const form = { current: initialForm };
  const seededHostnamesRef = { current: initialForm };
  const effect = liftHookCallback(COMPONENT_PATH, 'useEffect', 'hostnameSettings.dockerLookup');
  const push = (hostnamesEnabled, hostnameSettings) =>
    bindLifted(effect, {
      hostnamesEnabled,
      hostnameSettings,
      seededHostnamesRef,
      setHostnameForm: (value) => {
        form.current = typeof value === 'function' ? value(form.current) : value;
      }
    })();
  return { form, push };
};

const STORED_SETTINGS = {
  resolver: '10.0.0.53',
  guestAccess: true,
  routerLookup: false,
  dockerLookup: false
};

test('the hostnames form opens on the stored settings, not the built-in defaults', () => {
  const sourceFile = parseComponent();
  const ref = findSoleNode(
    sourceFile,
    'seededHostnamesRef declaration',
    (node) =>
      typescript.isVariableDeclaration(node) &&
      node.name.getText(sourceFile) === 'seededHostnamesRef'
  );
  assert.equal(
    ref.initializer.getText(sourceFile),
    'useRef<HostnameForm>(hostnameForm)',
    'an empty start matches no field, so the defaults would stay and Save would write them'
  );

  const { form, push } = hostnameReset();
  push(true, STORED_SETTINGS);

  assert.deepEqual(form.current, { enabled: true, ...STORED_SETTINGS });
});

test('typing in the hostnames form survives a change pushed from another tab', () => {
  const { form, push } = hostnameReset();
  push(false, { ...STORED_SETTINGS, guestAccess: false });

  form.current = { ...form.current, resolver: '10.0.0.99' };
  // Another tab turns guest access on, and this tab reads the settings again.
  push(false, STORED_SETTINGS);

  assert.equal(form.current.resolver, '10.0.0.99', 'what the user typed stays');
  assert.equal(
    form.current.guestAccess,
    true,
    'a field the user left alone takes the new value, so Save does not write the old one back'
  );

  // This tab's own save then reads back what it wrote.
  push(false, { ...STORED_SETTINGS, resolver: '10.0.0.99' });
  assert.deepEqual(form.current, { enabled: false, ...STORED_SETTINGS, resolver: '10.0.0.99' });
});

test('removing a rule and adding it back is not an unsaved change', async () => {
  const card = exclusionsCard();
  await card.load([rule('10.0.0.5'), rule('10.0.0.5', { traffic: 'prefill', mode: 'hide' })]);

  card.remove(card.state.draft[0].rowId);
  assert.deepEqual(withoutRowIds(card.state.draft), [
    rule('10.0.0.5', { traffic: 'prefill', mode: 'hide' })
  ]);
  card.addRules(['10.0.0.5'], 'all');
  assert.equal(card.state.draft.length, 2);

  assert.equal(card.unsaved(), false, 'the same rules in another order are the same list');
  const readsBefore = card.reads.length;
  card.reloadExclusions();
  assert.equal(card.reads.length, readsBefore + 1, "another admin's changes still reach the card");
});

test('an edit made while the exclusions save runs stays on the card as unsaved', async () => {
  const card = exclusionsCard();
  await card.load([rule('10.0.0.5')]);
  card.changeMode(card.state.draft[0].rowId, 'hide');

  const saving = card.save();
  card.addRules(['10.0.0.6'], 'all');
  card.saves[0].resolve({ ips: [], rules: [rule('10.0.0.5', { mode: 'hide' })] });
  await saving;

  assert.deepEqual(withoutRowIds(card.state.draft), [
    rule('10.0.0.5', { mode: 'hide' }),
    rule('10.0.0.6')
  ]);
  assert.deepEqual(card.state.saved, [rule('10.0.0.5', { mode: 'hide' })]);
  assert.equal(card.unsaved(), true);
});

test('a prefill rule leaves its address free for an all-traffic rule in the client picker', () => {
  const options = bindLifted(liftHookCallback(COMPONENT_PATH, 'useMemo', 'resolveClientLabel'), {
    allClientIps: ['10.0.0.8', '10.0.0.7'],
    getGroupForIp: () => undefined,
    resolveClientLabel: (ip) => ({ text: ip, substitutesAddress: false }),
    getHostnameForIp: () => null,
    excludedRules: [
      { ...rule('10.0.0.7', { traffic: 'prefill', mode: 'hide' }), rowId: 1 },
      { ...rule('10.0.0.8', { traffic: 'prefill', mode: 'hide' }), rowId: 2 },
      { ...rule('10.0.0.8'), rowId: 3 }
    ],
    t: (key) => key
  })();

  assert.deepEqual(
    options.map(({ value, description, disabled }) => ({ value, description, disabled })),
    [
      { value: '10.0.0.7', description: undefined, disabled: false },
      { value: '10.0.0.8', description: 'management.sections.clients.modeExclude', disabled: true }
    ]
  );
});

test('a background reload keeps the row numbers, so an open service picker stays open', async () => {
  const card = exclusionsCard();
  await card.load([rule('10.0.0.5'), rule('', { traffic: 'prefill' })]);
  const ids = card.ids();
  assert.ok(ids.every((id) => typeof id === 'number'));

  card.reloadExclusions();
  await card.answerLatestRead([rule('10.0.0.5'), rule('', { traffic: 'prefill' })]);
  assert.deepEqual(card.ids(), ids);

  card.reloadExclusions();
  await card.answerLatestRead([
    rule('10.0.0.5'),
    rule('', { traffic: 'prefill' }),
    rule('10.0.0.6')
  ]);
  assert.deepEqual(card.ids().slice(0, 2), ids);
  assert.equal(ids.includes(card.ids()[2]), false);
});

test('two rows edited to the same rule save as one and leave nothing unsaved', async () => {
  const card = exclusionsCard();
  await card.load([rule('10.0.0.5'), rule('10.0.0.5', { mode: 'hide' })]);
  card.changeMode(card.state.draft[1].rowId, 'exclude');

  const saving = card.save();
  assert.deepEqual(card.sent, [[rule('10.0.0.5')]], 'the server is sent one copy');
  card.saves[0].resolve({ ips: [], rules: [rule('10.0.0.5')] });
  await saving;

  assert.deepEqual(withoutRowIds(card.state.draft), [rule('10.0.0.5')]);
  assert.equal(card.unsaved(), false);

  card.reloadExclusions();
  await card.answerLatestRead([rule('10.0.0.5'), rule('10.0.0.6')]);
  assert.deepEqual(withoutRowIds(card.state.draft), [rule('10.0.0.5'), rule('10.0.0.6')]);
});

test('an edit undone while the exclusions save runs stays undone', async () => {
  const card = exclusionsCard();
  await card.load([rule('10.0.0.5')]);
  const [row] = card.state.draft;
  card.changeMode(row.rowId, 'hide');

  const saving = card.save();
  card.changeMode(row.rowId, 'exclude');
  const readsBefore = card.reads.length;
  card.reloadExclusions();
  assert.equal(card.reads.length, readsBefore, 'no reload starts while the save runs');

  // A read started some other way, Retry for one, answers before the save does.
  const retry = card.loadExcludedIps(false);
  card.reads.at(-1).resolve({ ips: [], rules: [rule('10.0.0.5', { mode: 'hide' })] });
  await retry;
  card.saves[0].resolve({ ips: [], rules: [rule('10.0.0.5', { mode: 'hide' })] });
  await saving;

  assert.deepEqual(withoutRowIds(card.state.draft), [rule('10.0.0.5')], 'the undo is on the card');
  assert.equal(card.unsaved(), true, 'and it shows as unsaved against what the server kept');
});

test('a reload begun before a save never replaces the save answer', async () => {
  const card = exclusionsCard();
  await card.load([rule('10.0.0.5')]);
  const background = card.loadExcludedIps(false);
  const held = card.reads.at(-1);

  card.changeMode(card.state.draft[0].rowId, 'hide');
  const saving = card.save();
  card.addRules(['10.0.0.6'], 'all');
  card.saves[0].resolve({ ips: [], rules: [rule('10.0.0.5', { mode: 'hide' })] });
  await saving;

  card.remove(card.state.draft[1].rowId);
  held.resolve({ ips: [], rules: [rule('10.0.0.5')] });
  await background;

  assert.deepEqual(card.state.saved, [rule('10.0.0.5', { mode: 'hide' })]);
  assert.deepEqual(withoutRowIds(card.state.draft), [rule('10.0.0.5', { mode: 'hide' })]);
  assert.equal(card.unsaved(), false);
});

test('typing an address that is already listed adds no second row, whatever its mode', async () => {
  const card = exclusionsCard();
  await card.load([rule('10.0.0.5', { mode: 'hide' }), rule('10.0.0.6', { traffic: 'prefill' })]);
  const typeAndAdd = (text) =>
    card.handler('handleAddExcluded', {
      excludeInput: text,
      parseIpCandidates: (value) => value.split(/[\s,]+/).filter(Boolean),
      isValidIpAddress: () => true,
      setExcludeInput: () => undefined
    })();

  typeAndAdd('10.0.0.5');
  assert.equal(card.state.draft.length, 2);

  typeAndAdd('10.0.0.6');
  assert.deepEqual(
    withoutRowIds(card.state.draft).at(-1),
    rule('10.0.0.6'),
    'a prefill rule for the address does not stand in for an all-traffic one'
  );
});

// Renders the exclusion rows from the map the component ships, with stand-ins for the shared
// components so their props can be read.
const renderExclusionRows = (rows) => {
  const sourceFile = parseComponent();
  const list = findSoleNode(
    sourceFile,
    'exclusion row list',
    (node) =>
      typescript.isCallExpression(node) &&
      node.expression.getText(sourceFile) === 'excludedRules.map'
  );
  const components = {};
  for (const name of [
    'SegmentedControl',
    'MultiSelectDropdown',
    'Tooltip',
    'Button',
    'ClientIpDisplay',
    'Trash2'
  ]) {
    components[name] = function StandIn() {
      return null;
    };
  }
  const rendered = bindLifted(
    `() => (${list.getText(sourceFile)})`,
    {
      React,
      ...components,
      excludedRules: rows,
      t: (key) => key,
      PERSISTENT_PREFILL_SERVICES: prefillServices(),
      handleChangeMode: () => undefined,
      handleChangeTraffic: () => undefined,
      handleChangeServices: () => undefined,
      handleRemoveExcluded: () => undefined
    },
    { jsx: typescript.JsxEmit.React }
  )();

  return rendered.map((element) => {
    const elements = [];
    const visit = (node) => {
      if (Array.isArray(node)) {
        node.forEach(visit);
        return;
      }
      if (!React.isValidElement(node)) return;
      elements.push(node);
      visit(node.props.children);
    };
    visit(element);
    const ofType = (name) => elements.filter((child) => child.type === components[name]);
    const trafficControl = ofType('SegmentedControl').find((control) =>
      control.props.options.some((option) => option.value === 'prefill')
    );
    return { key: element.key, elements, ofType, trafficControl };
  });
};

test('each row picks its traffic, and a prefill row picks its services', () => {
  const [anyAddress, address] = renderExclusionRows([
    { ...rule('', { traffic: 'prefill', services: ['Epic'] }), rowId: 7 },
    { ...rule('10.0.0.5'), rowId: 8 }
  ]);

  assert.deepEqual([anyAddress.key, address.key], ['7', '8'], 'rows are keyed by row number');

  assert.equal(
    anyAddress.trafficControl.props.options.find((option) => option.value === 'all').disabled,
    true,
    'a rule with no address can only be a prefill rule'
  );
  assert.ok(
    anyAddress.elements.some(
      (element) => element.props.children === 'management.sections.clients.anyAddress'
    )
  );
  const [picker] = anyAddress.ofType('MultiSelectDropdown');
  assert.deepEqual(picker.props.values, ['Epic']);
  assert.equal(picker.props.placeholder, 'management.sections.clients.allServices');
  assert.deepEqual(
    picker.props.options.map((option) => option.value),
    ['Steam', 'Epic', 'Xbox', 'BattleNet', 'Riot']
  );

  assert.equal(
    address.trafficControl.props.options.every((option) => !option.disabled),
    true,
    'an address rule can be either'
  );
  assert.equal(
    address.ofType('MultiSelectDropdown').length,
    0,
    'an all-traffic rule has no services'
  );
});
