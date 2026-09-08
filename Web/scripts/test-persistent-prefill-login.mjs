import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import typescript from 'typescript';
import { bindLifted, liftHookCallback, parseSource } from './transpile-module.mjs';

const path =
  'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillPersistentCard.tsx';
const source = parseSource(path, typescript.ScriptKind.TSX);
const body = source.statements
  .filter((node) => !typescript.isImportDeclaration(node))
  .map((node) => node.getText(source))
  .join('\n')
  .replace('export function', 'function');
const compiled = typescript.transpileModule(body, {
  compilerOptions: {
    target: typescript.ScriptTarget.ES2022,
    jsx: typescript.JsxEmit.React,
    jsxFactory: 'h',
    jsxFragmentFactory: 'Fragment'
  }
}).outputText;
const en = JSON.parse(
  readFileSync(new URL('../src/i18n/locales/en.json', import.meta.url), 'utf8')
);
const zh = JSON.parse(
  readFileSync(new URL('../src/i18n/locales/zh.json', import.meta.url), 'utf8')
);
const translate = (key, values = {}) =>
  Object.entries(values).reduce(
    (text, [name, value]) => text.replace(`{{${name}}}`, value),
    key.split('.').reduce((value, name) => value?.[name], en) ?? key
  );
const components = Object.fromEntries(
  [
    'ActionMenu',
    'ActionMenuDangerItem',
    'ActionMenuDivider',
    'ActionMenuItem',
    'Button',
    'Card',
    'Alert',
    'Badge',
    'Tooltip',
    'CollapsibleRegion',
    'LoadingSpinner',
    'StatusDot',
    'ChevronDown'
  ].map((name) => [name, name])
);
const bindings = {
  ...components,
  h: (type, props, ...children) => ({ type, props: props ?? {}, children }),
  Fragment: 'fragment',
  useEffect: () => undefined,
  useRef: (value) => ({ current: value }),
  useState: (value) => [value, () => undefined],
  useId: () => 'container-settings',
  useTranslation: () => ({ t: translate }),
  useFormattedDateTime: () => '',
  usePersistentLoginStoreState: () => ({ sessionUnavailableState: null }),
  useActivityStatus: () => ({ isActive: () => true }),
  isScheduledPrefillAnonymousService: (key) => key === 'battleNet',
  getPersistentServiceId: (key) => key,
  SCHEDULED_PREFILL_BUTTON_SIZE: 'sm',
  formatTimeRemaining: () => '',
  formatBytes: () => ''
};
const render = new Function(
  ...Object.keys(bindings),
  `${compiled}\nreturn ScheduledPrefillPersistentCard;`
)(...Object.values(bindings));
const flatten = (node) => {
  if (node === null || node === undefined || typeof node === 'boolean') return [];
  if (Array.isArray(node)) return node.flatMap(flatten);
  if (typeof node !== 'object') return [node];
  return [node, ...node.children.flatMap(flatten)];
};
const card = (props = {}) =>
  flatten(
    render({
      serviceKey: 'steam',
      selectedGamesCount: 1,
      scheduleEnabled: true,
      container: { isRunning: true, isAuthenticated: false, isPrefilling: false },
      ...props
    })
  );
const text = (nodes) =>
  nodes
    .flatMap((node) =>
      typeof node === 'string' ? [node] : node?.type === 'Tooltip' ? [node.props.content] : []
    )
    .join(' ');
for (const [reason, sentence] of [
  ['account-required', 'Sign in with your own LANCache account'],
  ['no-saved-login', 'No usable login is saved for this LANCache account'],
  ['unknown', 'Your saved login could not be checked'],
  [null, 'Your saved login could not be checked']
]) {
  test(`saved login explains ${reason} without displaying an unavailable account`, () => {
    const nodes = card({
      integrationLoginAvailability: { available: false, account: 'other-account', reason }
    });
    assert.ok(text(nodes).includes(sentence));
    assert.equal(text(nodes).includes('other-account'), false);
  });
}

test('available current-account login displays its own username', () => {
  const nodes = card({
    integrationLoginAvailability: { available: true, account: 'my-steam-login', reason: null }
  });
  assert.ok(text(nodes).includes('Saved login: my-steam-login.'));
});

test('loading and authentication display their own status', () => {
  const loading = card({ integrationLoginAvailabilityLoading: true });
  assert.ok(text(loading).includes('Checking your saved login'));
  assert.ok(loading.some((node) => node.type === 'Button' && node.props.disabled));
  const busy = card({
    authenticating: true,
    integrationLoginAvailability: { available: false, reason: 'no-saved-login' }
  });
  assert.ok(busy.some((node) => node.type === 'LoadingSpinner' && node.props.inline));
});

test('REST false flags override stale activity and remove download state', () => {
  const stopped = card({
    container: { isRunning: false, isAuthenticated: false, isPrefilling: false }
  });
  assert.ok(text(stopped).includes(translate('prefill.persistent.status.stopped')));
  assert.ok(
    stopped.some(
      (node) =>
        node.type === 'Button' &&
        node.children.includes(translate('prefill.persistent.actions.start'))
    )
  );
  const loggedOut = card();
  assert.ok(text(loggedOut).includes(translate('prefill.persistent.status.notLoggedIn')));
  assert.equal(
    loggedOut.some((node) => node.props?.role === 'progressbar'),
    false
  );
});

test('new locale entries match in both languages and preserve placeholders', () => {
  for (const key of [
    'savedLoginChecking',
    'savedLoginAccountRequired',
    'savedLoginMissing',
    'savedLoginUnknown',
    'savedLoginAvailable'
  ]) {
    const prefix = [
      'management',
      'schedules',
      'services',
      'scheduledPrefill',
      'config',
      'persistentContainers',
      key
    ];
    const english = prefix.reduce((value, name) => value[name], en);
    const chinese = prefix.reduce((value, name) => value[name], zh);
    assert.equal(typeof chinese, 'string');
    assert.deepEqual(chinese.match(/{{\w+}}/g), english.match(/{{\w+}}/g));
  }
});

test('availability responses from a previous LANCache account are discarded', async () => {
  const identity = { current: 'first-account' };
  let finish;
  let visible = new Map();
  const load = bindLifted(
    liftHookCallback(
      'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillConfigModal.tsx',
      'useCallback',
      'const requestIdentity = privateAvailabilityIdentityRef.current'
    ),
    {
      privateAvailabilityIdentityRef: identity,
      canUseSavedLogin: true,
      requiresIndividualAccount: false,
      SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS: ['steam'],
      getPersistentServiceId: (key) => key,
      ApiService: {
        getPersistentIntegrationLoginAvailability: () =>
          new Promise((resolve) => {
            finish = resolve;
          })
      },
      setIntegrationLoginAvailabilityByService: (value) => {
        visible = value;
      },
      setIntegrationLoginAvailabilityIdentity: () => undefined,
      setLoadingIntegrationLoginAvailability: () => undefined,
      isAbortError: () => false
    }
  );
  const request = load();
  identity.current = 'second-account';
  finish({ available: true, account: 'first-steam-login', reason: null });
  await request;
  assert.equal(visible.size, 0);
});

test('sign in again opens the existing modal and successful login refreshes server state', () => {
  const managerSource = parseSource(
    'src/components/features/management/steam/SteamLoginManager.tsx',
    typescript.ScriptKind.TSX
  );
  const managerBody = managerSource.statements
    .filter((node) => !typescript.isImportDeclaration(node) && !typescript.isExportAssignment(node))
    .map((node) => node.getText(managerSource))
    .join('\n');
  const managerCompiled = typescript.transpileModule(managerBody, {
    compilerOptions: {
      target: typescript.ScriptTarget.ES2022,
      jsx: typescript.JsxEmit.React,
      jsxFactory: 'h',
      jsxFragmentFactory: 'Fragment'
    }
  }).outputText;
  const states = [];
  let success;
  let refreshes = 0;
  const modes = [];
  const managerBindings = {
    ...bindings,
    ...Object.fromEntries(
      [
        'User',
        'UserCheck',
        'HelpPopover',
        'HelpSection',
        'HelpNote',
        'HelpDefinition',
        'SegmentedControl',
        'SteamAuthModal'
      ].map((name) => [name, name])
    ),
    useState: (initial) => {
      const slot = { value: initial };
      states.push(slot);
      return [
        initial,
        (value) => {
          slot.value = value;
        }
      ];
    },
    useSteamAuth: () => ({
      steamAuthMode: 'authenticated',
      username: 'mapping-login',
      refreshSteamAuth: () => {
        refreshes++;
      },
      setSteamAuthMode: (value) => {
        modes.push(value);
      }
    }),
    useSteamAuthentication: (options) => {
      success = options.onSuccess;
      return { state: { loading: false }, actions: {} };
    }
  };
  const manager = new Function(
    ...Object.keys(managerBindings),
    `${managerCompiled}\nreturn SteamLoginManager;`
  )(...Object.values(managerBindings));
  const nodes = flatten(manager({ authMode: 'authenticated', mockMode: false }));
  const button = nodes.find(
    (node) => node.type === 'Button' && node.children.includes('Sign in again')
  );
  assert.ok(button);
  button.props.onClick();
  assert.equal(states[0].value, true);
  assert.ok(nodes.some((node) => node.type === 'SteamAuthModal'));
  success('Signed in');
  assert.equal(states[0].value, false);
  assert.equal(refreshes, 1);
  assert.deepEqual(modes, []);
});
