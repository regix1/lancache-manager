import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import {
  bindLifted,
  findSoleNode,
  liftHookCallback,
  parseSource,
  transpile
} from './transpile-module.mjs';

const componentPath = 'src/components/features/prefill/PrefillHomePage.tsx';
const panelPath = 'src/components/features/prefill/PrefillPanel.tsx';
const eventPath = 'src/components/features/prefill/hooks/usePrefillEventHandlers.ts';
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
    onSessionEnd: () => ended.push('ended')
  };
  const ownerEnded = bindLifted(eventCallback('SessionEnded'), bindings);

  ownerEnded({ sessionId: 'steam-session', reason: 'ended' });
  assert.ok(state.some(([name, value]) => name === 'session' && value === null));
  assert.equal(startButtons(render((service) => starts.push(service))).length, 1);

  ownerEnded({ sessionId: 'steam-session', reason: 'duplicate' });
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

test('a failed End Session or Cancel Login raises one popup and logs the reason', async () => {
  const failure = new Error('Hub down');
  for (const [invoked, key] of [
    ["'EndSessionAsync'", 'prefill.errors.endSessionFailed'],
    ["'CancelLoginAsync'", 'prefill.errors.cancelLoginFailed']
  ]) {
    const notified = [];
    const logged = [];
    await bindLifted(liftHookCallback(panelPath, 'useCallback', invoked), {
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
      addLog: (type, message) => logged.push([type, message]),
      getErrorMessage: (error) => error.message,
      notifyError: (...popup) => notified.push(popup),
      t: (text) => text
    })();
    assert.deepEqual(notified, [[key, failure]], invoked);
    assert.ok(
      logged.some(([type, message]) => type === 'error' && message === 'Hub down'),
      invoked
    );
  }
});
