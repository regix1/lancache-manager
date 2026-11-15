import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import React from 'react';
import { make, parseSource } from './transpile-module.mjs';

/**
 * A sign-in test sends the browser away and back to `?accessSetup=1`, and the app then shows the
 * sign-in wizard. A dialog fades in and out: it is drawn with `opened` set while it is open, and
 * when the user closes it, it stays drawn with `opened={false}` so the fade-out plays. That holds
 * whatever screen the wizard closes onto, including the depot setup screen of a fresh install and
 * the sign-in screen of a browser that is not signed in.
 *
 * The app's shipped content component runs here on a small hook store. Every component it draws is
 * a stub, and the test reads the props the wizard element is drawn with, so it does not care how
 * the app keeps its open state or where in the tree it draws the wizard.
 */

const COMPONENT = 'src/App.tsx';

/** Draws nothing; the test reads the props it was given from the element tree. */
const stub = (name) => {
  const component = () => null;
  Object.defineProperty(component, 'name', { value: name });
  return component;
};

/** Every name the app imports, so a stub stands in for any the test does not bind itself. */
const importedNames = parseSource(COMPONENT)
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

const AccessSetup = stub('AccessSetup');
const DepotInitializationModal = stub('DepotInitializationModal');
const AuthenticationModal = stub('AuthenticationModal');

const turn = () => new Promise((resolve) => setImmediate(resolve));

/**
 * Runs the app's content on one hook store after the return from a sign-in test redirect, with
 * the sign-in state `auth` and the installation state `setupStatus`.
 */
const mountApp = async (auth, setupStatus) => {
  globalThis.window = {
    location: {
      pathname: '/',
      search: '?accessSetup=1',
      href: 'http://lancache.test/?accessSetup=1'
    },
    history: { replaceState: () => undefined },
    addEventListener: () => undefined,
    removeEventListener: () => undefined
  };
  globalThis.fetch = async () => ({
    ok: true,
    status: 200,
    json: async () => ({ database: { totalMappings: 0 } })
  });

  const slots = [];
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
  const signalR = { on: () => undefined, off: () => undefined, isConnected: true };
  const AppContent = make(COMPONENT, 'AppContent', {
    ...Object.fromEntries(importedNames.map((name) => [name, stub(name)])),
    React: { ...React, ...hooks },
    ...hooks,
    require: () => ({}),
    lazy: () => stub('LazyTab'),
    Suspense: stub('Suspense'),
    useTranslation: () => ({ t: (key) => key }),
    useErrorHandler: () => ({ notifyError: () => undefined }),
    useSetupStatus: () => ({ setupStatus, isLoading: false }),
    useAuth: () => auth,
    // The sign-in and depot gates are present exactly while open; this test reads only the wizard.
    useExitPresence: (open) => ({ present: open, closing: false }),
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
    AccessSetup,
    DepotInitializationModal,
    AuthenticationModal
  });

  const render = () => {
    cursor = 0;
    effects = [];
    return AppContent({});
  };

  // The first draw, its effects (the depot status check ends the loading screen), then the
  // draw the person sees once the checks have answered.
  render();
  effects.forEach((effect) => effect());
  await turn();
  await turn();

  /** The elements of `type` the app draws now. */
  const drawn = (type) => {
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
    walk(render());
    return found;
  };
  return { drawn };
};

const SCREENS = {
  'the depot setup screen of a fresh install': {
    screen: DepotInitializationModal,
    auth: {
      authMode: 'authenticated',
      sessionId: 'admin-session',
      hasSession: true,
      isLoading: false,
      refreshAuth: async () => undefined,
      prefillEnabled: true,
      isBanned: false,
      authenticationEnabled: true,
      accountMode: 'password'
    },
    setupStatus: {
      isCompleted: false,
      hasProcessedLogs: false,
      accountExists: true,
      needsPostgresCredentials: false,
      mainAdminRecoveryAvailable: false
    }
  },
  'the sign-in screen of a browser that is not signed in': {
    screen: AuthenticationModal,
    auth: {
      authMode: 'unauthenticated',
      sessionId: null,
      hasSession: false,
      isLoading: false,
      refreshAuth: async () => undefined,
      prefillEnabled: false,
      isBanned: false,
      authenticationEnabled: true,
      accountMode: 'password'
    },
    setupStatus: {
      isCompleted: true,
      hasProcessedLogs: true,
      accountExists: true,
      needsPostgresCredentials: false,
      mainAdminRecoveryAvailable: false
    }
  }
};

for (const [name, { screen, auth, setupStatus }] of Object.entries(SCREENS)) {
  test(`the sign-in wizard closing onto ${name} stays drawn with opened false`, async () => {
    const originalWarn = console.warn;
    console.warn = () => undefined;
    try {
      const app = await mountApp(auth, setupStatus);
      const whileOpen = app.drawn(AccessSetup);
      assert.equal(
        whileOpen.length,
        1,
        'returning with ?accessSetup=1 does not draw the sign-in wizard'
      );
      assert.equal(
        whileOpen[0].opened,
        true,
        `the open sign-in wizard is drawn with opened ${String(whileOpen[0].opened)}, not true`
      );
      assert.equal(
        typeof whileOpen[0].onClose,
        'function',
        'the open sign-in wizard has no way to close'
      );

      whileOpen[0].onClose();
      assert.equal(app.drawn(screen).length, 1, `closing the sign-in wizard does not show ${name}`);
      const afterClose = app.drawn(AccessSetup);
      assert.equal(
        afterClose.length,
        1,
        `closing the sign-in wizard onto ${name} removes it at once, so it vanishes with no fade-out`
      );
      assert.equal(
        afterClose[0].opened,
        false,
        `the closed sign-in wizard is drawn with opened ${String(afterClose[0].opened)}, not false`
      );
    } finally {
      console.warn = originalWarn;
      delete globalThis.window;
      delete globalThis.fetch;
    }
  });
}
