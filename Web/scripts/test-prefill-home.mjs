import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import i18next from 'i18next';
import typescript from 'typescript';
import {
  bindLifted,
  collectNodes,
  compileToUrl,
  findSoleNode,
  liftHookCallback,
  moduleUrl,
  parseSource,
  transpile
} from './transpile-module.mjs';

const componentPath = 'src/components/features/prefill/PrefillHomePage.tsx';
const panelPath = 'src/components/features/prefill/PrefillPanel.tsx';
const eventPath = 'src/components/features/prefill/hooks/usePrefillEventHandlers.ts';
const signalRPath = 'src/components/features/prefill/hooks/usePrefillSignalR.ts';
const prefillTypes = await import(
  await compileToUrl('../src/components/features/prefill/hooks/prefillTypes.ts')
);
const source = parseSource(componentPath, typescript.ScriptKind.TSX);

const functionSource = (name) =>
  findSoleNode(
    source,
    `${name} function`,
    (node) => typescript.isFunctionDeclaration(node) && node.name?.getText(source) === name
  ).getText(source);

const Button = Symbol('Button');
const Fragment = Symbol('Fragment');
const services = ['steam', 'epic', 'battlenet', 'riot', 'xbox'].map((id) => ({
  id,
  displayName: id,
  icon: Symbol(`${id} icon`),
  homeCardClass: `prefill-service-card--${id}`,
  homeDescriptionKey: `prefill.home.${id}.description`,
  homeFeatureKeys: [`prefill.home.${id}.feature`],
  homeLoginNoteKey: `prefill.home.${id}.loginNote`
}));

const h = (type, props, ...children) => ({
  type,
  props: {
    ...props,
    children: children.length <= 1 ? children[0] : children
  }
});

const compiled = transpile(
  `${functionSource('ServiceFeatureList')}\n${functionSource('PrefillHomePage').replace(/^export\s+/, '')}`,
  typescript.ModuleKind.CommonJS,
  {
    jsx: typescript.JsxEmit.React,
    jsxFactory: 'h',
    jsxFragmentFactory: 'Fragment'
  }
);

const PrefillHomePage = new Function(
  'useEffect',
  'useMemo',
  'useState',
  'useTranslation',
  'useMediaQuery',
  'PREFILL_SERVICES',
  'Button',
  'CollapsibleRegion',
  'Shield',
  'ChevronDown',
  'h',
  'Fragment',
  `${compiled}\nreturn PrefillHomePage;`
)(
  (effect) => effect(),
  (create) => create(),
  (initial) => [initial, () => undefined],
  () => ({ t: (key) => key }),
  () => false,
  services,
  Button,
  Symbol('CollapsibleRegion'),
  Symbol('Shield'),
  Symbol('ChevronDown'),
  h,
  Fragment
);

const render = (onServiceStart, overrides = {}) =>
  PrefillHomePage({
    onServiceStart,
    isAdmin: false,
    steamPrefillEnabled: true,
    epicPrefillEnabled: false,
    battlenetPrefillEnabled: false,
    riotPrefillEnabled: false,
    xboxPrefillEnabled: false,
    ...overrides
  });

const visit = (value, found = []) => {
  if (Array.isArray(value)) {
    value.forEach((item) => visit(item, found));
    return found;
  }
  if (!value || typeof value !== 'object') return found;
  found.push(value);
  visit(value.props?.children, found);
  return found;
};

const startButtons = (tree) =>
  visit(tree).filter(
    (node) => node.type === Button && node.props.children === 'prefill.home.startSession'
  );

const eventCallback = (eventName) => {
  const eventSource = parseSource(eventPath, typescript.ScriptKind.TSX);
  const registration = findSoleNode(eventSource, `${eventName} registration`, (node) => {
    if (!typescript.isCallExpression(node) || node.arguments.length < 2) return false;
    if (!typescript.isPropertyAccessExpression(node.expression)) return false;
    return (
      node.expression.name.getText(eventSource) === 'on' &&
      node.arguments[0].getText(eventSource).includes(`'${eventName}'`) &&
      typescript.isArrowFunction(node.arguments[1])
    );
  });
  return registration.arguments[1].getText(eventSource);
};

test('one-service guest waits for the existing Start Session button', () => {
  const starts = [];
  const tree = render((service) => starts.push(service));

  assert.equal(starts.length, 0);
  assert.notEqual(tree, null);
  const buttons = startButtons(tree);
  assert.equal(buttons.length, 1);

  buttons[0].props.onClick();
  assert.deepEqual(starts, ['steam']);

  render((service) => starts.push(service));
  render((service) => starts.push(service));
  assert.deepEqual(starts, ['steam']);
});

test('the owner terminal callback returns to Start without replacement', () => {
  const starts = [];
  const ended = [];
  const state = [];
  const bindings = {
    addLog: () => undefined,
    t: (key) => key,
    setSession: (value) => state.push(['session', value]),
    setIsLoggedIn: (value) => state.push(['loggedIn', value]),
    setIsPrefillActive: (value) => state.push(['active', value]),
    clearCancelTracking: () => undefined,
    stopAnimations: () => undefined,
    setPrefillProgress: (value) => state.push(['progress', value]),
    clearAllPrefillStorage: () => undefined,
    onSessionEnd: () => ended.push('ended'),
    endSession: () => {
      state.push(['session', null]);
      ended.push('ended');
    }
  };
  const ownerEnded = bindLifted(eventCallback('SessionEnded'), bindings);

  ownerEnded({
    sessionId: 'steam-session',
    reason: 'ended',
    reasonKey: 'signalr.prefillSession.stopped'
  });
  assert.ok(state.some(([name, value]) => name === 'session' && value === null));
  assert.equal(startButtons(render((service) => starts.push(service))).length, 1);

  ownerEnded({
    sessionId: 'steam-session',
    reason: 'duplicate',
    reasonKey: 'signalr.prefillSession.stopped'
  });
  assert.equal(startButtons(render((service) => starts.push(service))).length, 1);
  assert.equal(starts.length, 0);
  assert.equal(ended.length, 2);
});

test('grant filtering and create-error retry remain explicit', () => {
  const starts = [];
  const multiple = render((service) => starts.push(service), { epicPrefillEnabled: true });
  assert.equal(startButtons(multiple).length, 2);

  const none = render((service) => starts.push(service), { steamPrefillEnabled: false });
  assert.equal(startButtons(none).length, 0);

  const retry = startButtons(render((service) => starts.push(service)));
  assert.equal(retry.length, 1);
  retry[0].props.onClick();
  assert.deepEqual(starts, ['steam']);
});

test('no card claims Ready or draws its own error box', () => {
  const nodes = visit(
    render(() => undefined, { epicPrefillEnabled: true, xboxPrefillEnabled: true })
  );
  assert.equal(nodes.filter((node) => node.props?.children === 'prefill.home.ready').length, 0);
  assert.equal(nodes.filter((node) => node.props?.className === 'prefill-service-error').length, 0);
});

test('a failed or overtaken settings read never shows values nobody saved', async () => {
  const shownOS = [];
  const errors = [];
  const replies = [];
  const load = bindLifted(liftHookCallback(panelPath, 'useCallback', 'setMaxThreadLimit'), {
    API_BASE: '/api',
    fetch: () => new Promise((resolve) => replies.push(resolve)),
    assertOk: async (response) => {
      if (!response.ok) throw new Error('HTTP 502');
      return response;
    },
    getErrorMessage: (error) => error.message,
    defaultsRequestRef: { current: 0 },
    setSelectedOS: (value) => shownOS.push(value),
    setMaxThreadLimit: () => undefined,
    setMaxConcurrency: () => undefined,
    setDefaultsError: (value) => errors.push(value)
  });
  const saved = (operatingSystems) => ({ ok: true, json: async () => ({ operatingSystems }) });

  const overtaken = load();
  const current = load();
  replies[1](saved(['linux']));
  await current;
  replies[0](saved(['windows']));
  await overtaken;
  assert.deepEqual(shownOS, [['linux']]);

  const failed = load();
  replies[2]({ ok: false, status: 502 });
  await failed;
  assert.deepEqual(errors, [null, 'HTTP 502']);
});

test('a reconnect reload clears a failed settings read and brings the controls back', async () => {
  const panel = parseSource(panelPath, typescript.ScriptKind.TSX);
  const reconnect = findSoleNode(
    panel,
    'settings reload on reconnect',
    (node) =>
      typescript.isCallExpression(node) &&
      node.expression.getText(panel) === 'useReconnectRefetch' &&
      node.arguments[1]?.getText(panel).includes('loadPrefillDefaults')
  );
  const replies = [
    { ok: false, status: 502 },
    { ok: true, json: async () => ({ operatingSystems: ['linux'] }) }
  ];
  let shownError;
  const load = bindLifted(liftHookCallback(panelPath, 'useCallback', 'setMaxThreadLimit'), {
    API_BASE: '/api',
    fetch: async () => replies.shift(),
    assertOk: async (response) => {
      if (!response.ok) throw new Error('HTTP 502');
      return response;
    },
    getErrorMessage: (error) => error.message,
    defaultsRequestRef: { current: 0 },
    setSelectedOS: () => undefined,
    setMaxThreadLimit: () => undefined,
    setMaxConcurrency: () => undefined,
    setDefaultsError: (value) => {
      shownError = value;
    }
  });

  await load();
  assert.equal(shownError, 'HTTP 502');
  let reload;
  bindLifted(reconnect.arguments[1].getText(panel), {
    loadPrefillDefaults: () => (reload = load())
  })();
  await reload;
  // A null error is what swaps the box back to the command controls.
  assert.equal(shownError, null);
});

test('a failed settings save raises one popup, and a guest sends no save', async () => {
  const failure = new Error('Server refused');
  const sent = [];
  const notified = [];
  const save = (isAdmin) =>
    bindLifted(liftHookCallback(panelPath, 'useCallback', 'body.operatingSystems'), {
      isAdmin,
      API_BASE: '/api',
      fetch: async (...request) => {
        sent.push(request);
        throw failure;
      },
      ApiService: {
        getJsonFetchOptions: () => ({}),
        updatePrefillDefaults: async (body) => {
          sent.push(body);
          throw failure;
        }
      },
      notifyError: (...popup) => notified.push(popup),
      t: (key) => key
    });

  await save(false)(['linux']);
  assert.equal(sent.length, 0);
  await save(true)(['linux']);
  assert.deepEqual(notified, [['prefill.errors.saveSettingsFailed', failure]]);
});

test('a failed End Session or Cancel Login reaches an admin as one popup and a guest as one log line', async () => {
  const failure = new Error('Hub down');
  for (const [invoked, key] of [
    ["'EndSessionAsync'", 'prefill.errors.endSessionFailed'],
    ["'CancelLoginAsync'", 'prefill.errors.cancelLoginFailed']
  ]) {
    for (const isAdmin of [true, false]) {
      const notified = [];
      const logged = [];
      await bindLifted(liftHookCallback(panelPath, 'useCallback', invoked), {
        isAdmin,
        signalR: {
          session: { id: 'session-a' },
          hubConnection: {
            current: {
              invoke: async () => {
                throw failure;
              }
            }
          }
        },
        setShowAuthModal: () => undefined,
        authActions: { resetAuthForm: () => undefined },
        authLoginAttemptRef: { current: null },
        addLog: (type, message) => logged.push([type, message]),
        getErrorMessage: (error) => error.message,
        notifyError: (...popup) => notified.push(popup),
        t: (text) => text
      })();
      const errorLines = logged.filter(([type]) => type === 'error');
      assert.deepEqual(notified, isAdmin ? [[key, failure]] : [], `${invoked} admin ${isAdmin}`);
      // The hub's error is an English sentence, so a guest gets the same key the admin's popup shows.
      assert.deepEqual(errorLines, isAdmin ? [] : [['error', key]], `${invoked} admin ${isAdmin}`);
    }
  }
});

test('an End Session the server answers with a failure key reaches an admin as one popup and a guest as one log line', async () => {
  for (const isAdmin of [true, false]) {
    const notified = [];
    const logged = [];
    await bindLifted(liftHookCallback(panelPath, 'useCallback', "'EndSessionAsync'"), {
      isAdmin,
      signalR: {
        session: { id: 'session-a' },
        hubConnection: {
          current: {
            invoke: async () => ({
              stageKey: 'errors.prefill.containerRemovalFailed',
              context: { service: 'Steam' }
            })
          }
        }
      },
      addLog: (type, message) => logged.push([type, message]),
      getErrorMessage: (error) => error.message,
      notifyError: (...popup) => notified.push(popup),
      t: (key, values) => (values ? `${key}|${values.service}` : key)
    })();
    const shown = 'errors.prefill.containerRemovalFailed|Steam';
    assert.deepEqual(notified, isAdmin ? [[shown]] : [], `admin ${isAdmin}`);
    assert.deepEqual(
      logged.filter(([type]) => type === 'error'),
      isAdmin ? [] : [['error', shown]],
      `admin ${isAdmin}`
    );
  }
});

test('Cancel Login logs a cancel only when the sign-in was canceled', async () => {
  for (const [answer, expected] of [
    [false, []],
    [true, [['info', 'prefill.log.loginCancelled']]]
  ]) {
    const logged = [];
    await bindLifted(liftHookCallback(panelPath, 'useCallback', "'CancelLoginAsync'"), {
      isAdmin: true,
      signalR: {
        session: { id: 'session-a' },
        hubConnection: { current: { invoke: async () => answer } }
      },
      setShowAuthModal: () => undefined,
      authActions: { resetAuthForm: () => undefined },
      authLoginAttemptRef: { current: null },
      addLog: (type, message) => logged.push([type, message]),
      getErrorMessage: (error) => error.message,
      notifyError: () => undefined,
      t: (text) => text
    })();
    assert.deepEqual(logged, expected, `hub answered ${answer}`);
  }
});

test("the person's Cancel names the dialog's own sign-in once it has one", async () => {
  for (const [attempt, expected] of [
    [7, [['CancelLoginAttemptAsync', 'session-a', 7]]],
    [null, [['CancelLoginAsync', 'session-a']]]
  ]) {
    const invoked = [];
    await bindLifted(liftHookCallback(panelPath, 'useCallback', "'CancelLoginAsync'"), {
      isAdmin: true,
      signalR: {
        session: { id: 'session-a' },
        hubConnection: {
          current: {
            invoke: async (...args) => {
              invoked.push(args);
              return true;
            }
          }
        }
      },
      setShowAuthModal: () => undefined,
      authActions: { resetAuthForm: () => undefined },
      authLoginAttemptRef: { current: attempt },
      addLog: () => undefined,
      getErrorMessage: (error) => error.message,
      notifyError: () => undefined,
      t: (text) => text
    })();
    assert.deepEqual(invoked, expected, `attempt ${attempt}`);
  }
});

test('an expiring session writes no second error beside the expired panel', () => {
  const written = [];
  const countdown = bindLifted(
    liftHookCallback(panelPath, 'useEffect', 'hasExpiredRef.current = true'),
    {
      signalR: {
        session: { status: 'Active', expiresAt: '2000-01-01T00:00:00Z' },
        setTimeRemaining: () => undefined,
        setIsLoggedIn: () => undefined,
        setError: (value) => written.push(value)
      },
      hasExpiredRef: { current: false },
      parseUtcDate: (value) => new Date(value),
      setInterval: (tick) => {
        tick();
        return 0;
      },
      clearInterval: () => undefined,
      t: (key) => key
    }
  );
  countdown();
  assert.deepEqual(written, []);
});

test('a session with no time left logs no "expires in" line, and one with time left still does', async () => {
  const hookPath = 'src/components/features/prefill/hooks/usePrefillSignalR.ts';
  const noop = () => undefined;
  const session = (secondsLeft) => ({
    id: 'session-a',
    status: 'Active',
    authState: 'Authenticated',
    containerName: 'prefill-a',
    activeRunCount: 0,
    expiresAt: new Date(Date.now() + secondsLeft * 1000).toISOString()
  });
  const connection = (current) => ({
    state: 'Connected',
    invoke: async (name) =>
      name === 'GetMySessions'
        ? [current]
        : name === 'OpenSessionAsync'
          ? { session: current, stageKey: null, context: null }
          : null
  });
  const shared = (logged) => ({
    addLog: (_type, message) => logged.push(message),
    t: (key) => key,
    setHubConnectFailed: noop,
    setSession: noop,
    setTimeRemaining: noop,
    setIsLoggedIn: noop,
    formatTimeRemaining: (seconds) => `${seconds}s`,
    serviceId: 'steam',
    serviceNameKey: 'steam',
    getErrorMessage: (error) => error.message
  });
  const adopt = (current, logged) =>
    bindLifted(liftHookCallback(hookPath, 'useCallback', "'GetMySessions'"), {
      ...shared(logged),
      initializationAttempted: { current: false },
      setIsInitializing: noop,
      connectToHub: async () => connection(current),
      sessionRef: { current: null },
      supportsConcurrentPrefill: () => false,
      updateRuns: noop,
      rehydratePrefillProgress: async () => undefined,
      setIsPrefillActive: noop,
      setPrefillProgress: noop,
      sessionStore: { getItem: () => null, removeItem: noop },
      STORAGE_KEYS: {},
      clearAllPrefillStorage: noop
    })();
  const create = (current, logged) =>
    bindLifted(liftHookCallback(hookPath, 'useCallback', "'OpenSessionAsync'"), {
      ...shared(logged),
      setIsCreating: noop,
      setCreateSessionError: noop,
      hubConnection: { current: connection(current) },
      connectToHub: async () => null,
      notifyError: noop
    })(noop);

  for (const start of [adopt, create]) {
    for (const [secondsLeft, lines] of [
      [-60, 0],
      [0, 0],
      [3600, 1]
    ]) {
      const logged = [];
      await start(session(secondsLeft), logged);
      assert.equal(
        logged.filter((message) => message === 'prefill.log.sessionExpiresIn').length,
        lines,
        `${start.name} with ${secondsLeft}s left`
      );
    }
  }
});

test('a create the server refuses with a key shows that key translated with its values', async () => {
  const hookPath = 'src/components/features/prefill/hooks/usePrefillSignalR.ts';
  const noop = () => undefined;
  const shown = [];
  const logged = [];
  const sessions = [];
  const create = bindLifted(liftHookCallback(hookPath, 'useCallback', "'OpenSessionAsync'"), {
    addLog: (_type, message) => logged.push(message),
    t: (key, values) => `${key}|${values?.service ?? ''}`,
    setHubConnectFailed: noop,
    setSession: (session) => sessions.push(session),
    setTimeRemaining: noop,
    setIsLoggedIn: noop,
    formatTimeRemaining: (seconds) => `${seconds}s`,
    serviceId: 'steam',
    serviceNameKey: 'steam',
    getErrorMessage: (error) => error.message,
    setIsCreating: noop,
    setCreateSessionError: (message) => shown.push(message),
    hubConnection: {
      current: {
        state: 'Connected',
        invoke: async () => ({
          session: null,
          stageKey: 'errors.prefill.containerRemovalFailed',
          context: { service: 'Steam' }
        })
      }
    },
    connectToHub: async () => null,
    notifyError: noop
  });

  await create(noop);

  assert.deepEqual(sessions, []);
  assert.deepEqual(shown, [null, 'errors.prefill.containerRemovalFailed|Steam']);
  assert.ok(logged.includes('errors.prefill.containerRemovalFailed|Steam'));
});

test('a Prefill wrapper whose content can be empty renders only with that content', () => {
  // An empty wrapper still takes its container's 1rem gap, which pushed the controls column
  // below the Activity Log and left a gap under the header title on phones.
  const panel = parseSource(panelPath, typescript.ScriptKind.TSX);
  const conditionOf = (className) => {
    const opening = findSoleNode(
      panel,
      `${className} wrapper`,
      (node) =>
        typescript.isJsxOpeningElement(node) &&
        node.attributes.properties.some(
          (attribute) => attribute.getText(panel) === `className="${className}"`
        )
    );
    let node = opening.parent.parent;
    while (typescript.isParenthesizedExpression(node)) node = node.parent;
    assert.ok(typescript.isBinaryExpression(node), `${className} is rendered unconditionally`);
    return node.left.getText(panel);
  };
  assert.match(conditionOf('prefill-sec-network'), /networkDiagnostics/);
  assert.match(conditionOf('flex items-center gap-3 w-full sm:w-auto'), /!isSessionExpired/);
  assert.match(conditionOf('prefill-sec-progress space-y-3'), /shownRuns\.length/);
});

test('dismissed finished runs keep no run list, and the settings box carries its own spacing', () => {
  const panel = parseSource(panelPath, typescript.ScriptKind.TSX);
  const declaration = findSoleNode(
    panel,
    'shown run list',
    (node) => typescript.isVariableDeclaration(node) && node.name.getText(panel) === 'shownRuns'
  );
  const run = (runId, state) => ({
    runId,
    sessionId: 'session-a',
    snapshot: { startedAt: '', state }
  });
  const shownRuns = (runs, dismissed) =>
    bindLifted(`() => ${declaration.initializer.getText(panel)}`, {
      signalR: { runs, session: { id: 'session-a' } },
      runCompletions: [],
      isPrefillRunActive: (item) => item.snapshot.state === 'running',
      isRunCompletionDismissed: (item) => dismissed.includes(item.runId)
    })();
  assert.equal(shownRuns([run('done', 'completed')], ['done']).length, 0);
  assert.equal(shownRuns([run('done', 'completed')], []).length, 1);
  assert.equal(shownRuns([run('live', 'running')], ['live']).length, 1);

  // The box hides under the connection banner; a wrapper around it would stay behind, empty.
  const settingsBox = findSoleNode(
    panel,
    'settings load box',
    (node) =>
      typescript.isJsxSelfClosingElement(node) &&
      node.tagName.getText(panel) === 'ErrorBlock' &&
      node.getText(panel).includes('failedLoadSettings')
  );
  assert.ok(
    settingsBox.attributes.properties.some(
      (attribute) => attribute.getText(panel) === 'className="prefill-sec-commands"'
    ),
    'the settings box does not carry the commands section class'
  );
});

test('Start new session sits in the expired panel action slot, right and centered like Retry', () => {
  const panel = parseSource(panelPath, typescript.ScriptKind.TSX);
  const startNew = findSoleNode(
    panel,
    'Start new session button',
    (node) =>
      (typescript.isJsxOpeningElement(node) || typescript.isJsxSelfClosingElement(node)) &&
      node.tagName.getText(panel) === 'Button' &&
      node.getText(panel).includes('onClick={handleStartNewSession}')
  );
  let node = startNew.parent;
  while (node && !typescript.isJsxAttribute(node)) node = node.parent;
  assert.ok(node, 'the button is not passed as an attribute');
  assert.equal(node.name.getText(panel), 'action');
  assert.equal(node.parent.parent.tagName.getText(panel), 'Alert');
});

const localeFile = (name) =>
  JSON.parse(readFileSync(new URL(`../src/i18n/locales/${name}.json`, import.meta.url), 'utf8'));
const keyText = (locale, key) => key.split('.').reduce((value, part) => value?.[part], locale);

test('an errored session is never described as expired, in the notice or in the refused command', () => {
  const panel = parseSource(panelPath, typescript.ScriptKind.TSX);
  const errored = findSoleNode(
    panel,
    'isSessionErrored declaration',
    (node) =>
      typescript.isVariableDeclaration(node) && node.name.getText(panel) === 'isSessionErrored'
  );
  const isErrored = (status) =>
    bindLifted(`() => ${errored.initializer.getText(panel)}`, {
      signalR: { session: { status } }
    })();
  assert.equal(isErrored('Error'), true);
  assert.equal(isErrored('Active'), false);
  assert.equal(isErrored('Terminated'), false);

  const keysIn = (node) =>
    collectNodes(node, typescript.isStringLiteralLike).map((literal) => literal.text);
  const choices = collectNodes(
    panel,
    (node) =>
      typescript.isConditionalExpression(node) &&
      node.condition.getText(panel) === 'isSessionErrored'
  ).map((node) => [node.whenTrue.getText(panel), node.whenFalse.getText(panel)]);
  assert.deepEqual(choices, [
    ["'prefill.sessionErrored.title'", "'prefill.sessionExpired.title'"],
    ["'prefill.sessionErrored.message'", "'prefill.sessionExpired.message'"]
  ]);
  assert.ok(keysIn(panel).includes('prefill.errors.sessionErrored'));

  const refusal = async (session, timeRemaining) => {
    const logged = [];
    await bindLifted(
      liftHookCallback(panelPath, 'useCallback', "'prefill.errors.sessionExpired'"),
      {
        signalR: {
          session,
          timeRemaining,
          hubConnection: { current: {} },
          isPrefillActive: false,
          runs: []
        },
        addLog: (type, message) => logged.push([type, message]),
        t: (key) => key
      }
    )('prefill');
    return logged;
  };
  return Promise.all([
    refusal({ status: 'Error' }, 600),
    refusal({ status: 'Active' }, 0),
    refusal({ status: 'Terminated' }, 600)
  ]).then(([whenErrored, whenOutOfTime, whenTerminated]) => {
    assert.deepEqual(whenErrored, [['warning', 'prefill.errors.sessionErrored']]);
    assert.deepEqual(whenOutOfTime, [['warning', 'prefill.errors.sessionExpired']]);
    assert.deepEqual(whenTerminated, [['warning', 'prefill.errors.sessionExpired']]);
  });
});

test('a failed run logs its own reason key, and the new keys read in both languages', () => {
  const events = parseSource(eventPath);
  const call = findSoleNode(
    events,
    'PrefillStateChanged handler',
    (node) =>
      typescript.isCallExpression(node) &&
      node.expression.getText(events) === 'connection.on' &&
      node.arguments[0]?.getText(events).includes("'PrefillStateChanged'")
  );
  // A run ends with the per-run event, whose run carries the reason the run card draws.
  const runLogged = [];
  const runHandler = bindLifted(call.arguments[1].getText(events), {
    rehydratePrefillProgress: () => undefined,
    connection: undefined,
    sessionRef: { current: { id: 's' } },
    getPrefillRunReasonKey: prefillTypes.getPrefillRunReasonKey,
    serviceId: 'steam',
    addLog: (type, message) => runLogged.push([type, message]),
    t: (key) => key
  });
  const runEvent = (state, reason) => ({
    sessionId: 's',
    state,
    operationId: 'run-1',
    daemonInstanceId: 'daemon-1',
    run: { snapshot: { state, reason } }
  });
  runHandler(runEvent('failed', 'stalled'));
  runHandler(runEvent('failed', 'no-such-reason'));
  runHandler(runEvent('completed', null));
  assert.deepEqual(runLogged, [['error', 'signalr.scheduledPrefill.failedStalled']]);

  const en = localeFile('en');
  const zh = localeFile('zh');
  for (const key of [
    'prefill.sessionErrored.title',
    'prefill.sessionErrored.message',
    'prefill.errors.sessionErrored',
    'signalr.prefillSession.stopped',
    'signalr.prefillSession.expired',
    'signalr.prefillSession.serviceShutdown',
    'prefill.log.sessionEndedOnServer'
  ]) {
    assert.equal(typeof keyText(en, key), 'string', `${key} in en`);
    assert.equal(typeof keyText(zh, key), 'string', `${key} in zh`);
    assert.notEqual(keyText(zh, key), keyText(en, key), `${key} is translated`);
  }
});

test('every reason a daemon or the manager stores on a failed run names a key for the run card and the log', () => {
  const events = parseSource(eventPath);
  const call = findSoleNode(
    events,
    'PrefillStateChanged handler',
    (node) =>
      typescript.isCallExpression(node) &&
      node.expression.getText(events) === 'connection.on' &&
      node.arguments[0]?.getText(events).includes("'PrefillStateChanged'")
  );
  const en = localeFile('en');
  const zh = localeFile('zh');
  for (const [service, reason, key] of [
    // A run in which a game failed: Battle.net and Xbox send download-failed, Epic and Riot prefill-failed.
    ['battlenet', 'download-failed', 'errors.prefill.requestFailed'],
    ['xbox', 'download-failed', 'errors.prefill.requestFailed'],
    ['epic', 'prefill-failed', 'errors.prefill.requestFailed'],
    ['riot', 'prefill-failed', 'errors.prefill.requestFailed'],
    // A Start the daemon refused, stored with the refusal's code.
    ['steam', 'run-limit', 'errors.prefill.runLimit'],
    ['epic', 'operation-conflict', 'errors.prefill.operationConflict'],
    ['xbox', 'daemon-outdated', 'errors.prefill.daemonOutdated'],
    ['steam', 'game-details-unavailable', 'errors.steam.gameDetailsUnavailable']
  ]) {
    // The run card and the activity dialog read the same function.
    assert.equal(prefillTypes.getPrefillRunReasonKey(reason, service), key, `${service} ${reason}`);
    const logged = [];
    bindLifted(call.arguments[1].getText(events), {
      rehydratePrefillProgress: () => undefined,
      connection: undefined,
      sessionRef: { current: { id: 's' } },
      getPrefillRunReasonKey: prefillTypes.getPrefillRunReasonKey,
      serviceId: service,
      addLog: (type, message) => logged.push([type, message]),
      t: (text) => text
    })({
      sessionId: 's',
      state: 'failed',
      operationId: 'run-1',
      daemonInstanceId: 'daemon-1',
      run: { snapshot: { state: 'failed', reason } }
    });
    assert.deepEqual(logged, [['error', key]], `${service} ${reason} log`);
    assert.equal(typeof keyText(en, key), 'string', `${key} in en`);
    assert.equal(typeof keyText(zh, key), 'string', `${key} in zh`);
  }
});

test("the owner's log shows the stop reason through its key", () => {
  const logged = [];
  const ended = bindLifted(eventCallback('SessionEnded'), {
    addLog: (type, message) => logged.push([type, message]),
    t: (key, values) => (values ? `${key}(${values.reason})` : `${key}!`),
    setSession: () => undefined,
    setIsLoggedIn: () => undefined,
    setIsPrefillActive: () => undefined,
    clearCancelTracking: () => undefined,
    stopAnimations: () => undefined,
    setPrefillProgress: () => undefined,
    clearAllPrefillStorage: () => undefined,
    onSessionEnd: () => undefined,
    endSession: () => undefined
  });
  ended({ sessionId: 's', reason: 'Session expired', reasonKey: 'signalr.prefillSession.expired' });
  assert.deepEqual(logged, [
    ['warning', 'prefill.log.sessionEnded(signalr.prefillSession.expired!)']
  ]);
});

const olderSession = (overrides = {}) => ({
  id: 's',
  status: 'Active',
  authState: 'Authenticated',
  expiresAt: new Date(Date.now() + 3600 * 1000).toISOString(),
  features: [],
  ...overrides
});

// The resubscribe outcomes both resubscribe paths must handle. `subscribes` lists each SubscribeToSessionAsync
// answer in order; `listed` is what GetMySessions answers (null: the read fails); `stateAfter` is the socket
// state once the first subscribe fails.
const resubscribeCases = [
  // The server no longer has the session: the page drops it.
  {
    name: 'unlisted',
    subscribes: [false],
    listed: [],
    stateAfter: 'Connected',
    ended: 1,
    subscribed: 1,
    reads: 0
  },
  // A slow or timed-out reply for a session the server still has: subscribe again, then read the runs.
  {
    name: 'listed',
    subscribes: [false, true],
    listed: ['s'],
    stateAfter: 'Connected',
    ended: 0,
    subscribed: 2,
    reads: 1
  },
  {
    name: 'listed, second subscribe fails',
    subscribes: [false, false],
    listed: ['s'],
    stateAfter: 'Connected',
    ended: 0,
    subscribed: 2,
    reads: 0
  },
  {
    name: 'list read fails',
    subscribes: [false],
    listed: null,
    stateAfter: 'Connected',
    ended: 0,
    subscribed: 1,
    reads: 0
  },
  {
    name: 'socket dropped',
    subscribes: [false],
    listed: [],
    stateAfter: 'Reconnecting',
    ended: 0,
    subscribed: 1,
    reads: 0
  }
];

const resubscribeConnection = (outcome) => {
  const calls = [];
  const answers = [...outcome.subscribes];
  const connection = {
    state: 'Connected',
    calls,
    invoke: async (name) => {
      calls.push(name);
      if (name === 'SubscribeToSessionAsync') {
        if (answers.shift()) return null;
        if (calls.length === 1) connection.state = outcome.stateAfter;
        throw new Error('Invocation timed out');
      }
      if (name === 'GetMySessions') {
        if (connection.state !== 'Connected' || outcome.listed === null)
          throw new Error('read failed');
        return outcome.listed.map((id) => olderSession({ id }));
      }
      return null;
    }
  };
  return connection;
};

const settle = async () => {
  for (let turn = 0; turn < 20; turn++) await new Promise((resolve) => setImmediate(resolve));
};

test('a reconnect drops the session only when the server no longer lists it, and resubscribes one it still has', async () => {
  const events = parseSource(eventPath);
  const call = findSoleNode(
    events,
    'onreconnected registration',
    (node) =>
      typescript.isCallExpression(node) &&
      node.expression.getText(events) === 'connection.onreconnected'
  );
  for (const outcome of resubscribeCases) {
    const logged = [];
    let ended = 0;
    let reads = 0;
    const connection = resubscribeConnection(outcome);
    const reconnected = bindLifted(call.arguments[0].getText(events), {
      addLog: (_type, message) => logged.push(message),
      t: (key) => key,
      sessionRef: { current: olderSession() },
      connection,
      rehydratePrefillProgress: async () => {
        reads++;
      },
      endSession: () => ended++
    });
    await reconnected();
    assert.equal(ended, outcome.ended, outcome.name);
    assert.equal(
      logged.filter((message) => message === 'prefill.log.sessionEndedOnServer').length,
      outcome.ended,
      outcome.name
    );
    assert.equal(
      connection.calls.filter((name) => name === 'SubscribeToSessionAsync').length,
      outcome.subscribed,
      outcome.name
    );
    assert.equal(reads, outcome.reads, outcome.name);
  }
});

test('a tab coming back into view drops the session only when the server no longer lists it, and resubscribes one it still has', async () => {
  for (const outcome of resubscribeCases) {
    const logged = [];
    let ended = 0;
    let reads = 0;
    let onVisible = null;
    const connection = resubscribeConnection(outcome);
    const effect = bindLifted(liftHookCallback(signalRPath, 'useEffect', "'visibilitychange'"), {
      document: {
        hidden: false,
        addEventListener: (_name, handler) => {
          onVisible = handler;
        },
        removeEventListener: () => undefined
      },
      setTimeout: (callback) => {
        callback();
        return 1;
      },
      clearTimeout: () => undefined,
      hubConnection: { current: connection },
      sessionRef: { current: olderSession() },
      rehydratePrefillProgress: async () => {
        reads++;
      },
      addLog: (_type, message) => logged.push(message),
      t: (key) => key,
      endSession: () => ended++
    });
    effect();
    onVisible();
    await settle();
    assert.equal(ended, outcome.ended, outcome.name);
    assert.equal(
      logged.filter((message) => message === 'prefill.log.sessionEndedOnServer').length,
      outcome.ended,
      outcome.name
    );
    assert.equal(
      connection.calls.filter((name) => name === 'SubscribeToSessionAsync').length,
      outcome.subscribed,
      outcome.name
    );
    assert.equal(reads, outcome.reads, outcome.name);
  }
});

test('a hub connect that finishes after the page left stops its socket and is never kept', async () => {
  let finishStart = null;
  const built = [];
  // Stands in for the SignalR class the hook constructs by that name.
  const hubConnectionClass = class {
    withUrl() {
      return this;
    }
    withAutomaticReconnect() {
      return this;
    }
    configureLogging() {
      return this;
    }
    build() {
      const connection = {
        state: 'Connecting',
        stopped: 0,
        on: () => undefined,
        onreconnecting: () => undefined,
        onreconnected: () => undefined,
        onclose: () => undefined,
        start: () =>
          new Promise((resolve) => {
            finishStart = () => {
              connection.state = 'Connected';
              resolve();
            };
          }),
        stop: async () => {
          connection.stopped++;
          connection.state = 'Disconnected';
        }
      };
      built.push(connection);
      return connection;
    }
  };
  const hubConnection = { current: null };
  const bindings = {
    hubConnection,
    connectInFlightRef: { current: null },
    mountedRef: { current: true },
    cancelWatchdogRef: { current: null },
    initializeSession: () => undefined,
    HubConnectionBuilder: hubConnectionClass,
    InfiniteBackoffRetryPolicy: class {},
    LogLevel: { Information: 1 },
    SIGNALR_BASE: '/hubs',
    hubPath: '/steam-daemon',
    registerPrefillEventHandlers: () => undefined,
    applyRunProgress: () => undefined,
    addLog: () => undefined,
    onAuthStateChanged: () => undefined,
    setSession: () => undefined,
    setTimeRemaining: () => undefined,
    setIsLoggedIn: () => undefined,
    setIsPrefillActive: () => undefined,
    setPrefillProgress: () => undefined,
    endSession: () => undefined,
    sessionRef: { current: null },
    isCancelling: { current: false },
    serviceId: 'steam',
    rehydratePrefillProgress: async () => undefined,
    setIsCancellingState: () => undefined,
    setIsConnecting: () => undefined,
    setIsConnected: () => undefined,
    setHubConnectFailed: () => undefined,
    updateRuns: () => undefined,
    runsRef: { current: [] },
    isPrefillRunActive: () => false,
    console: { warn: () => undefined }
  };
  const connect = bindLifted(
    liftHookCallback(signalRPath, 'useCallback', 'new HubConnectionBuilder()'),
    bindings
  );
  const mount = bindLifted(
    liftHookCallback(signalRPath, 'useEffect', 'initializeSession();'),
    bindings
  );
  const unmount = mount();
  const connecting = connect();
  await settle();
  // The page leaves while the socket is still connecting.
  unmount();
  finishStart();
  const result = await connecting;
  await settle();
  assert.equal(built.length, 1);
  assert.equal(built[0].stopped, 1, 'the socket opened after the page left is stopped');
  assert.equal(hubConnection.current, null, 'no hub connection is kept for a page that left');
  assert.equal(result, null);
});

test('a session whose features are not known yet offers Start when it has no active run', () => {
  const activeRun = { runId: 'a', snapshot: { state: 'downloading' } };
  const endedRun = { runId: 'b', snapshot: { state: 'completed' } };
  assert.equal(prefillTypes.canStartPrefill({ features: [], runs: [] }), true);
  assert.equal(prefillTypes.canStartPrefill({ features: [] }), true);
  assert.equal(prefillTypes.canStartPrefill({ features: [], runs: [endedRun] }), true);
  assert.equal(prefillTypes.canStartPrefill({ features: [], runs: [activeRun] }), false);
});

test('a session with known features compares its active runs with the run limit', () => {
  const features = [
    'concurrentPrefill',
    'operationProgress',
    'targetedCancel',
    'inlineSelection',
    'activeOperations'
  ];
  const activeRun = { runId: 'a', snapshot: { state: 'downloading' } };
  const canStart = (session) => prefillTypes.canStartPrefill({ features, ...session });
  assert.equal(canStart({ maxConcurrentRuns: 2, runs: [activeRun] }), true);
  assert.equal(canStart({ maxConcurrentRuns: 1, runs: [activeRun] }), false);
  assert.equal(canStart({ maxConcurrentRuns: 2, activeRunCount: 2 }), false);
});

test('the panel reads whether a prefill is active from the run list alone', () => {
  const signalR = parseSource(signalRPath);
  const property = (name) =>
    findSoleNode(
      signalR,
      `${name} return property`,
      (node) =>
        typescript.isPropertyAssignment(node) &&
        node.name.getText(signalR) === name &&
        typescript.isReturnStatement(node.parent.parent)
    ).initializer.getText(signalR);
  const read = (name, runs) =>
    bindLifted(`() => (${property(name)})`, {
      session: olderSession(),
      isConnected: true,
      canStartPrefill: prefillTypes.canStartPrefill,
      isPrefillRunActive: prefillTypes.isPrefillRunActive,
      runs
    })();
  const activeRun = { runId: 'a', snapshot: { state: 'downloading' } };
  assert.equal(read('canStart', []), true);
  assert.equal(read('isPrefillActive', []), false);
  assert.equal(read('canStart', [activeRun]), false);
  assert.equal(read('isPrefillActive', [activeRun]), true);
});

test('the run a session row describes is the first active run, else the one that ended last', () => {
  const run = (runId, state, completedAtUtc) => ({ runId, snapshot: { state }, completedAtUtc });
  const { currentPrefillRun } = prefillTypes;
  assert.equal(
    currentPrefillRun([run('first', 'downloading'), run('second', 'downloading')]).runId,
    'first'
  );
  assert.equal(
    currentPrefillRun([
      run('ended', 'completed', '2026-10-28T16:00:00Z'),
      run('active', 'downloading')
    ]).runId,
    'active'
  );
  assert.equal(
    currentPrefillRun([
      run('old', 'completed', '2026-10-28T15:00:00Z'),
      run('new', 'failed', '2026-10-28T16:00:00Z')
    ]).runId,
    'new'
  );
  assert.equal(currentPrefillRun(undefined), undefined);
  assert.equal(currentPrefillRun([]), undefined);
});

test('two runs that end within the same second are told apart by their fractions', () => {
  const ended = (runId, completedAtUtc) => ({
    runId,
    snapshot: { state: 'completed' },
    completedAtUtc
  });
  const earlier = ended('earlier', '2026-10-28T16:00:00.5Z');
  const later = ended('later', '2026-10-28T16:00:00.51Z');
  assert.equal(prefillTypes.currentPrefillRun([earlier, later]).runId, 'later');
  assert.equal(prefillTypes.currentPrefillRun([later, earlier]).runId, 'later');
});

test("a refusal that names its key shows that key in the reader's language, never the English sentence", async () => {
  const en = localeFile('en');
  const zh = localeFile('zh');
  const translator = i18next.createInstance();
  await translator.init({
    resources: { en: { translation: en }, zh: { translation: zh } },
    lng: 'en',
    fallbackLng: 'en',
    supportedLngs: ['en', 'zh'],
    interpolation: { escapeValue: false }
  });
  globalThis.testTranslator = translator;
  const i18nStub = moduleUrl('export default globalThis.testTranslator;');
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
  const key = 'errors.prefill.daemonOutdated';
  const sentence = 'The English log sentence';
  const refusal = new ApiError({
    message: sentence,
    status: 409,
    kind: 'http',
    body: { stageKey: key }
  });
  for (const language of ['en', 'zh']) {
    await translator.changeLanguage(language);
    const shown = getErrorMessage(refusal);
    assert.equal(shown, translator.t(key), language);
    assert.notEqual(shown, sentence, language);
    assert.notEqual(shown, key, language);
  }
  assert.notEqual(zh.errors.prefill.daemonOutdated, en.errors.prefill.daemonOutdated);
  // A key the locale has no words for shows as its path, not as the server's sentence.
  const unknown = new ApiError({
    message: sentence,
    status: 409,
    kind: 'http',
    body: { stageKey: 'errors.prefill.noSuchKey' }
  });
  assert.equal(getErrorMessage(unknown), 'errors.prefill.noSuchKey');
});
