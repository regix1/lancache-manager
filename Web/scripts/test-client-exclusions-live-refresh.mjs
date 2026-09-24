import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
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
  const loadErrors = [];

  const loadExcludedIps = bindLifted(
    liftHookCallback(COMPONENT_PATH, 'useCallback', 'ApiService.getStatsExclusions'),
    {
      isAdmin: true,
      mockMode: false,
      hasExcludedChangesRef: editing,
      excludedRequestRef: { current: 0 },
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
  reads[1].resolve({ rules: [{ ip: '10.0.0.1', mode: 'exclude' }] });
  await newer;
  reads[0].reject(new Error('stale read failed'));
  await older;

  assert.deepEqual(loadErrors, [null], 'the stale failure never reaches the section');
  assert.deepEqual(shown, [[{ ip: '10.0.0.1', mode: 'exclude' }]]);
});

test('a superseded first read that fails leaves the list loading until the newer read answers', async () => {
  const loading = [];
  const loadErrors = [];
  const shown = [];
  const reads = [];

  const loadExcludedIps = bindLifted(
    liftHookCallback(COMPONENT_PATH, 'useCallback', 'ApiService.getStatsExclusions'),
    {
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

  reads[1].resolve({ rules: [{ ip: '10.0.0.1', mode: 'exclude' }] });
  await reconnect;

  assert.deepEqual(loading, [true, false], 'the newer read ends the loading the mount read began');
  assert.deepEqual(shown, [[{ ip: '10.0.0.1', mode: 'exclude' }]]);
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
