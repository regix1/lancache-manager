import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import typescript from 'typescript';
import {
  bindLifted,
  liftHookCallback,
  parseSource,
  compileToUrl,
  moduleUrl
} from './transpile-module.mjs';

const { supportsConcurrentPrefill, canStartPrefill, getPrefillRunProgress } = await import(
  await compileToUrl('../src/components/features/prefill/hooks/prefillTypes.ts')
);

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
const reasons = await import(await compileToUrl('../src/types.ts'));
const integrationReasonKeys = reasons.integrationReasonKeys;
const reactUrl = moduleUrl(`
export const useCallback = callback => callback;
export const useState = initial => [typeof initial === 'function' ? initial() : initial, () => undefined];
export const useLayoutEffect = () => undefined;
`);
const { useCountdownTimer } = await import(
  await compileToUrl('../src/hooks/useCountdownTimer.ts', { react: reactUrl })
);
const bindings = {
  integrationReasonKeys,
  getIntegrationReasonKey: reasons.getIntegrationReasonKey,
  useCountdownTimer,
  supportsConcurrentPrefill,
  canStartPrefill,
  getPrefillRunProgress,
  ...components,
  h: (type, props, ...children) => ({ type, props: props ?? {}, children }),
  Fragment: 'fragment',
  useEffect: () => undefined,
  useRef: (value) => ({ current: value }),
  useState: (value) => [value, () => undefined],
  useId: () => 'container-settings',
  useMediaQuery: () => false,
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
test('a container whose first status is still loading shows one loading line and no guessed status', () => {
  const nodes = card({ container: undefined, statusLoading: true });

  assert.equal(
    nodes.filter((node) => node.type === 'LoadingSpinner').length,
    1,
    'the body loading line is the one indicator'
  );
  assert.equal(nodes.filter((node) => node.type === 'StatusDot').length, 0);
  assert.ok(
    text(nodes).includes(
      en.management.schedules.services.scheduledPrefill.config.persistentContainers.loadingStatus
    )
  );
});

for (const [reason, sentence] of [
  ['account-required', en.errors.integration.accountRequired],
  ['no-saved-login', en.errors.integration.noSavedLogin]
]) {
  test(`saved login explains ${reason} without displaying an unavailable account`, () => {
    const nodes = card({
      integrationLoginAvailability: { available: false, account: 'other-account', reason }
    });
    assert.ok(text(nodes).includes(sentence));
    assert.equal(text(nodes).includes('other-account'), false);
  });
}

test('absent availability explains why saved-login reuse is disabled without displaying an account', () => {
  const nodes = card({ integrationLoginAvailability: undefined });
  assert.ok(text(nodes).includes(en.errors.integration.statusUnavailable));
  assert.equal(text(nodes).includes('other-account'), false);
  const reuse = nodes.find(
    (node) =>
      node.type === 'Button' &&
      node.children.includes(
        translate(
          'management.schedules.services.scheduledPrefill.config.persistentContainers.reuseIntegrationLogin'
        )
      )
  );
  assert.ok(reuse);
  assert.equal(reuse.props.disabled, true);
});

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

for (const [authExpiresAtUtc, expired] of [
  ['2099-01-01T00:00:00Z', false],
  ['2000-01-01T00:00:00Z', true]
])
  test(`the rendered countdown consumes absolute expiry ${authExpiresAtUtc}`, () => {
    const nodes = card({
      container: { isRunning: true, isAuthenticated: true, isPrefilling: false, authExpiresAtUtc }
    });
    assert.ok(text(nodes).includes(en.prefill.persistent.reloginRequiredBy));
    assert.equal(text(nodes).includes(en.prefill.persistent.signInExpired), expired);
    if (!expired)
      assert.ok(text(nodes).includes(translate('prefill.persistent.timeRemaining', { time: '' })));
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
  const availabilityIdentity = { current: '' };
  const errorsIdentity = { current: '' };
  let finish;
  let visible = new Map();
  let errors = {};
  const load = bindLifted(
    liftHookCallback(
      'src/components/features/management/schedules/scheduled-prefill/useScheduledPrefillContainers.ts',
      'useCallback',
      'const requestIdentity = privateAvailabilityIdentityRef.current'
    ),
    {
      privateAvailabilityIdentityRef: identity,
      integrationLoginRequestRef: { current: null },
      integrationLoginAvailabilityIdentityRef: availabilityIdentity,
      integrationLoginErrorsIdentityRef: errorsIdentity,
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
        visible = typeof value === 'function' ? value(visible) : value;
      },
      setIntegrationLoginErrors: (value) => {
        errors = typeof value === 'function' ? value(errors) : value;
      },
      setIntegrationLoginAvailabilityIdentity: (value) => {
        availabilityIdentity.current = value;
      },
      setIntegrationLoginErrorsIdentity: (value) => {
        errorsIdentity.current = value;
      },
      setLoadingIntegrationLoginAvailability: () => undefined,
      isAbortError: () => false,
      getErrorMessage: (error) => error.message
    }
  );
  const request = load();
  identity.current = 'second-account';
  finish({ available: true, account: 'first-steam-login', reason: null });
  await request;
  assert.equal(visible.size, 0);
  assert.deepEqual(errors, {});
  assert.equal(availabilityIdentity.current, '');
  assert.equal(errorsIdentity.current, '');
});

test('same-account availability keeps settled services and reports only failed reads', async () => {
  const identity = { current: 'current-account' };
  const availabilityIdentity = { current: 'current-account' };
  const errorsIdentity = { current: 'current-account' };
  let visible = new Map([['steam', { available: true, account: 'settled-steam', reason: null }]]);
  let errors = {};
  let loading = null;
  const load = bindLifted(
    liftHookCallback(
      'src/components/features/management/schedules/scheduled-prefill/useScheduledPrefillContainers.ts',
      'useCallback',
      'const requestIdentity = privateAvailabilityIdentityRef.current'
    ),
    {
      privateAvailabilityIdentityRef: identity,
      integrationLoginRequestRef: { current: null },
      integrationLoginAvailabilityIdentityRef: availabilityIdentity,
      integrationLoginErrorsIdentityRef: errorsIdentity,
      canUseSavedLogin: true,
      requiresIndividualAccount: false,
      SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS: ['steam', 'epicGames'],
      getPersistentServiceId: (key) => key,
      ApiService: {
        getPersistentIntegrationLoginAvailability: async (serviceKey) => {
          if (serviceKey === 'steam') throw new Error('steam unavailable');
          return { available: true, account: 'current-epic', reason: null };
        }
      },
      setIntegrationLoginAvailabilityByService: (value) => {
        visible = typeof value === 'function' ? value(visible) : value;
      },
      setIntegrationLoginErrors: (value) => {
        errors = typeof value === 'function' ? value(errors) : value;
      },
      setIntegrationLoginAvailabilityIdentity: (value) => {
        availabilityIdentity.current = value;
      },
      setIntegrationLoginErrorsIdentity: (value) => {
        errorsIdentity.current = value;
      },
      setLoadingIntegrationLoginAvailability: (value) => {
        loading = value;
      },
      isAbortError: () => false,
      getErrorMessage: (error) => error.message
    }
  );

  await load();

  assert.equal(loading, false);
  assert.equal(visible.get('steam').account, 'settled-steam');
  assert.equal(visible.get('epicGames').account, 'current-epic');
  assert.deepEqual(errors, { steam: 'steam unavailable' });
  assert.equal(availabilityIdentity.current, 'current-account');
  assert.equal(errorsIdentity.current, 'current-account');
});

test('sign in opens the existing modal and successful login refreshes server state', () => {
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
      access: { canManage: true, canSignIn: true, canLogout: true },
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
    },
    useAuth: () => ({
      authenticationEnabled: true,
      authMode: 'authenticated',
      accountId: 'a',
      sessionId: 'a'
    })
  };
  const manager = new Function(
    ...Object.keys(managerBindings),
    `${managerCompiled}\nreturn SteamLoginManager;`
  )(...Object.values(managerBindings));
  const nodes = flatten(manager({ authMode: 'authenticated', mockMode: false }));
  const button = nodes.find((node) => node.type === 'Button' && node.children.includes('Sign In'));
  assert.ok(button);
  button.props.onClick();
  assert.equal(states[0].value, true);
  assert.ok(nodes.some((node) => node.type === 'SteamAuthModal'));
  success('Signed in');
  assert.equal(states[0].value, false);
  assert.equal(refreshes, 1);
  assert.deepEqual(modes, []);
});
