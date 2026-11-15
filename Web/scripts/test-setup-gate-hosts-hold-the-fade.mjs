import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import React from 'react';
import { make, parseSource } from './transpile-module.mjs';

/**
 * Every full-screen gate fades out when its task succeeds: the sign-in card once the browser signs
 * in, the depot setup wizard once setup is done, and the first-run access setup and owner recovery
 * wizards once the installation has its account. A host that stops drawing a gate the moment its
 * task succeeds removes it in one frame, so each host keeps it drawn with `opened={false}` until
 * its fade-out has run, over the screen that follows. A status check that runs while the depot
 * wizard is open does not close it.
 *
 * The shipped App content and the shipped setup gate component run here on a small hook store.
 * Every component they draw is a stub, and the test reads the props each gate is drawn with. The
 * fade is a hook that keeps a closed gate present until the test lets the fade end.
 */

/** Draws nothing; the test reads the props it was given from the element tree. */
const stub = (name) => {
  const component = () => null;
  Object.defineProperty(component, 'name', { value: name });
  return component;
};

/** Every name `path` imports, so a stub stands in for any the test does not bind itself. */
const importedNames = (path) =>
  parseSource(path)
    .statements.filter(typescript.isImportDeclaration)
    .flatMap((declaration) => {
      const clause = declaration.importClause;
      if (!clause || clause.isTypeOnly) return [];
      const names = clause.name ? [clause.name.text] : [];
      const bindings = clause.namedBindings;
      if (bindings && typescript.isNamedImports(bindings))
        names.push(
          ...bindings.elements
            .filter((element) => !element.isTypeOnly)
            .map((element) => element.name.text)
        );
      return names;
    });

const turn = () => new Promise((resolve) => setImmediate(resolve));

/** A hook store for one component, with a fade hook whose closed gates stay until `endFades`. */
const hookStore = () => {
  const slots = [];
  const fades = [];
  let cursor = 0;
  let effects = [];
  const hooks = {
    useState: (initial) => {
      const index = cursor++;
      const slot = (slots[index] ??= {
        value: typeof initial === 'function' ? initial() : initial
      });
      return [
        slot.value,
        (next) => {
          slot.value = typeof next === 'function' ? next(slot.value) : next;
        }
      ];
    },
    useRef: (initial) => {
      const index = cursor++;
      return (slots[index] ??= { current: initial });
    },
    useMemo: (build) => {
      const index = cursor++;
      return (slots[index] ??= { value: build() }).value;
    },
    useCallback: (callback) => {
      cursor++;
      return callback;
    },
    useEffect: (effect) => {
      cursor++;
      effects.push(effect);
    },
    useLayoutEffect: (effect) => {
      cursor++;
      effects.push(effect);
    }
  };
  const useExitPresence = (open) => {
    const index = cursor++;
    const slot = (slots[index] ??= { present: open, open });
    slot.open = open;
    if (open) slot.present = true;
    if (!fades.includes(slot)) fades.push(slot);
    return { present: slot.present, closing: !open && slot.present };
  };
  return {
    hooks,
    useExitPresence,
    /** Starts one draw. */
    begin: () => {
      cursor = 0;
      effects = [];
    },
    runEffects: () => effects.forEach((effect) => effect()),
    /** Lets every running fade-out finish, as its timer would. */
    endFades: () => fades.forEach((slot) => (slot.present = slot.open))
  };
};

/** The props of every element of `type` in `tree`. */
const propsOf = (tree, type) => {
  const found = [];
  const walk = (node) => {
    if (Array.isArray(node)) {
      node.forEach(walk);
      return;
    }
    if (!React.isValidElement(node)) return;
    if (node.type === type) found.push(node.props);
    Object.values(node.props).forEach(walk);
  };
  walk(tree);
  return found;
};

const AuthenticationModal = stub('AuthenticationModal');
const DepotInitializationModal = stub('DepotInitializationModal');

/** Runs the app's content with sign-in state `auth` and installation state `setupStatus`. */
const mountApp = async (auth, setupStatus) => {
  let answerDepotCheck = true;
  globalThis.window = {
    location: { pathname: '/', search: '', href: 'http://lancache.test/' },
    history: { replaceState: () => undefined },
    addEventListener: () => undefined,
    removeEventListener: () => undefined
  };
  globalThis.fetch = () =>
    answerDepotCheck
      ? Promise.resolve({
          ok: true,
          status: 200,
          json: async () => ({ database: { totalMappings: 0 } })
        })
      : new Promise(() => undefined);

  const store = hookStore();
  const signalR = { on: () => undefined, off: () => undefined, isConnected: true };
  const AppContent = make('src/App.tsx', 'AppContent', {
    ...Object.fromEntries(importedNames('src/App.tsx').map((name) => [name, stub(name)])),
    React: { ...React, ...store.hooks },
    ...store.hooks,
    useExitPresence: store.useExitPresence,
    MODAL_EXIT_MS: 250,
    require: () => ({}),
    lazy: () => stub('LazyTab'),
    Suspense: stub('Suspense'),
    useTranslation: () => ({ t: (key) => key }),
    useErrorHandler: () => ({ notifyError: () => undefined }),
    useSetupStatus: () => ({ setupStatus, isLoading: false }),
    useAuth: () => auth,
    useSteamWebApiStatus: () => ({ status: null, refresh: async () => undefined }),
    useSteamAuth: () => ({ refreshSteamAuth: async () => undefined }),
    useDockerSocket: () => ({
      isDockerAvailable: true,
      error: null,
      refreshDockerStatus: async () => undefined
    }),
    useSignalR: () => signalR,
    useConfig: () => ({ refreshConfig: async () => undefined }),
    usesOidc: () => false,
    isAdminAccountRequired: () => false,
    isAbortError: () => false,
    ApiService: {
      getFetchOptions: (options) => ({ ...options }),
      getSchedules: async () => []
    },
    themeService: {
      reloadThemeAfterAuth: async () => undefined,
      setupPreferenceListeners: () => undefined
    },
    preferencesService: { setupSignalRListener: () => () => undefined },
    sessionStore: {
      getItem: () => null,
      setItem: () => undefined,
      removeItem: () => undefined
    },
    APP_EVENTS: { NAVIGATE_TO_TAB: 'navigate-to-tab', SHOW_FULL_SCAN_MODAL: 'show-full-scan' },
    DepotInitializationModal,
    AuthenticationModal
  });

  const render = () => {
    store.begin();
    return AppContent({});
  };
  /** Draws, runs the effects (the depot status check among them) and lets them answer. */
  const settle = async () => {
    render();
    store.runEffects();
    await turn();
    await turn();
  };
  await settle();
  return {
    drawn: (type) => propsOf(render(), type),
    settle,
    endFades: store.endFades,
    holdDepotChecks: () => (answerDepotCheck = false)
  };
};

const signedIn = {
  authMode: 'authenticated',
  sessionId: 'admin-session',
  hasSession: true,
  isLoading: false,
  refreshAuth: async () => undefined,
  prefillEnabled: true,
  isBanned: false,
  authenticationEnabled: true,
  accountMode: 'password'
};

const finishedInstall = () => ({
  isCompleted: true,
  hasProcessedLogs: true,
  accountExists: true,
  needsPostgresCredentials: false,
  mainAdminRecoveryAvailable: false
});

/** Checks the gate is drawn open, then closed and still drawn, then gone once the fade ends. */
const assertFadesOut = (gate, label, close, view) => {
  const open = view.drawn(gate);
  assert.equal(open.length, 1, `${label} is not drawn`);
  assert.equal(open[0].opened, true, `the open ${label} is drawn with opened ${open[0].opened}`);
  return close().then(() => {
    const closing = view.drawn(gate);
    assert.equal(
      closing.length,
      1,
      `${label} is removed the moment its task succeeds, so it vanishes with no fade-out`
    );
    assert.equal(closing[0].opened, false, `the closing ${label} is drawn with opened true`);
    view.endFades();
    assert.equal(view.drawn(gate).length, 0, `${label} is still drawn after its fade-out`);
  });
};

const quietly = async (run) => {
  const originalWarn = console.warn;
  console.warn = () => undefined;
  try {
    await run();
  } finally {
    console.warn = originalWarn;
    delete globalThis.window;
    delete globalThis.fetch;
  }
};

test('the sign-in card fades out over the page after the browser signs in', () =>
  quietly(async () => {
    const auth = {
      ...signedIn,
      authMode: 'unauthenticated',
      sessionId: null,
      hasSession: false,
      prefillEnabled: false
    };
    const app = await mountApp(auth, finishedInstall());
    await assertFadesOut(
      AuthenticationModal,
      'the sign-in card',
      async () => {
        Object.assign(auth, signedIn);
        await app.settle();
      },
      app
    );
  }));

test('the depot setup wizard stays open through a status check and fades out when setup ends', () =>
  quietly(async () => {
    const setupStatus = { ...finishedInstall(), isCompleted: false, hasProcessedLogs: false };
    const app = await mountApp({ ...signedIn }, setupStatus);
    await assertFadesOut(
      DepotInitializationModal,
      'the depot setup wizard',
      async () => {
        // Log processing marks the logs processed before setup is complete; the depot status check
        // that follows is still out when the wizard is read.
        app.holdDepotChecks();
        setupStatus.hasProcessedLogs = true;
        await app.settle();
        const checking = app.drawn(DepotInitializationModal);
        assert.equal(checking.length, 1, 'a status check mid-setup removed the depot wizard');
        assert.equal(checking[0].opened, true, 'a status check mid-setup closed the depot wizard');

        setupStatus.isCompleted = true;
        await app.settle();
      },
      app
    );
  }));

const SETUP_GATES = 'src/components/AppProviders.tsx';
const AccessSetup = stub('AccessSetup');
const SetupGate = stub('SetupGate');
const AdminAccountStep = stub('AdminAccountStep');
const TheApp = stub('TheApp');

/** Runs the shipped setup gate component with sign-in state `auth` and setup state `setup`. */
const mountSetupGates = (auth, setup) => {
  const store = hookStore();
  const useHeldValue = make('src/hooks/useHeldValue.ts', 'useHeldValue', {
    useState: store.hooks.useState
  });
  const AppSetup = make(SETUP_GATES, 'AppSetup', {
    ...Object.fromEntries(importedNames(SETUP_GATES).map((name) => [name, stub(name)])),
    React: { ...React, ...store.hooks },
    ...store.hooks,
    useExitPresence: store.useExitPresence,
    useHeldValue,
    MODAL_EXIT_MS: 250,
    useTranslation: () => ({ t: (key) => key }),
    useAuth: () => auth,
    useSetupStatus: () => setup,
    resolveInitialStep: () => 'admin-account',
    AccessSetup,
    SetupGate,
    AdminAccountStep
  });
  const children = React.createElement(TheApp);
  const render = () => {
    store.begin();
    return AppSetup({ children });
  };
  return {
    drawn: (type) => propsOf(render(), type),
    endFades: store.endFades
  };
};

const settingUp = () => ({
  setupStatus: { ...finishedInstall(), mainAdminRecoveryAvailable: false },
  isLoading: false,
  isSetupStatusKnown: true,
  error: null,
  refreshSetupStatus: async () => undefined
});

test('first-run access setup fades out over the app once the installation is set up', async () => {
  const auth = { isLoading: false, authenticationSetupRequired: true };
  const gates = mountSetupGates(auth, settingUp());
  assert.equal(gates.drawn(TheApp).length, 0, 'the app mounts while access setup is still up');
  await assertFadesOut(
    AccessSetup,
    'first-run access setup',
    async () => {
      auth.authenticationSetupRequired = false;
    },
    gates
  );
  assert.equal(gates.drawn(TheApp).length, 1, 'the app is not drawn once setup is done');
});

test('the owner recovery wizard fades out on the step it showed', async () => {
  const auth = { isLoading: false, authenticationSetupRequired: true };
  const setup = settingUp();
  setup.setupStatus.mainAdminRecoveryAvailable = true;
  const gates = mountSetupGates(auth, setup);
  assert.equal(
    gates.drawn(AdminAccountStep).length,
    1,
    'the recovery wizard shows no account step'
  );
  await assertFadesOut(
    SetupGate,
    'the owner recovery wizard',
    async () => {
      auth.authenticationSetupRequired = false;
      setup.setupStatus = { ...setup.setupStatus, mainAdminRecoveryAvailable: false };
      assert.equal(
        gates.drawn(AccessSetup).length,
        0,
        'the closing recovery wizard turned into access setup while it fades out'
      );
      assert.equal(
        gates.drawn(AdminAccountStep).length,
        1,
        'the closing recovery wizard dropped the step it showed while it fades out'
      );
    },
    gates
  );
});
