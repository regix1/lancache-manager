import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import React from 'react';
import { make, parseSource } from './transpile-module.mjs';

/**
 * Status reads answer in the order they start, because the auth service queues them. A read that a
 * 401 started can still carry the old cookie while the next read, started by a sign-in that posted
 * outside that queue (first account, main administrator recovery), is already on its way. The
 * older answer must not show the tab as signed out in between: only the newest read decides. When
 * the newest read gets no answer at all (a timeout, a dropped network, a gateway error), the older
 * answer is the last word the server gave, so the tab shows it.
 *
 * The shipped auth provider runs here on a small hook store. Every status read is held until the
 * test answers it, and the test reads the sign-in state the provider hands to the app.
 */

const AUTH_CONTEXT = 'src/contexts/AuthContext.tsx';
const AUTH_STATE_CHANGED = 'auth-state-changed';
const AUTH_SESSION_UPDATED = 'auth-session-updated';

/** Draws nothing; the test reads the props it was given from the element tree. */
const stub = (name) => {
  const component = () => null;
  Object.defineProperty(component, 'name', { value: name });
  return component;
};

/** Every name the provider imports, so a stub stands in for any the test does not bind itself. */
const importedNames = parseSource(AUTH_CONTEXT)
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

/** The status answer for a browser whose cookie holds `sessionId`, or none when it is null. */
const statusAnswer = (sessionId) => ({
  reachable: true,
  isAuthenticated: sessionId !== null,
  authenticationEnabled: true,
  accountMode: 'apiKeyPassword',
  sessionType: sessionId === null ? null : 'admin',
  sessionId,
  accountId: sessionId === null ? null : `account-of-${sessionId}`,
  isMainAdmin: false,
  expiresAt: null,
  prefillEnabled: false,
  prefillExpiresAt: null,
  epicPrefillEnabled: false,
  battlenetPrefillEnabled: false,
  riotPrefillEnabled: false,
  xboxPrefillEnabled: false,
  incompleteLoginServices: []
});

/** The stub the auth service returns for a status read that got no answer. */
const unanswered = { ...statusAnswer(null), reachable: false };

/** Runs the provider signed in on `old-session`; status reads are held until answered. */
const mountAuth = async () => {
  const listeners = {};
  const dispatched = [];
  globalThis.window = {
    addEventListener: (name, handler) => {
      listeners[name] = handler;
    },
    removeEventListener: () => undefined,
    dispatchEvent: (event) => {
      dispatched.push(event.type);
      return true;
    }
  };
  globalThis.Event = class {
    constructor(type) {
      this.type = type;
    }
  };

  const statusReads = [];
  const authService = {
    checkAuth: () =>
      new Promise((resolve) => {
        statusReads.push({ answer: resolve });
      }),
    login: async () => ({ success: false }),
    startGuestSession: async () => ({ success: false }),
    logout: async () => undefined
  };

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
      cursor++;
      return build();
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
  const AuthState = make(AUTH_CONTEXT, 'AuthProvider', {
    ...Object.fromEntries(importedNames.map((name) => [name, stub(name)])),
    React: { ...React, ...hooks },
    ...hooks,
    authService,
    isAccountHolder: (sessionType) => sessionType === 'admin' || sessionType === 'user',
    useSignalR: () => ({
      on: () => undefined,
      off: () => undefined,
      invoke: async () => undefined,
      isConnected: false
    }),
    AuthContext: { Provider: stub('AuthContext') },
    APP_EVENTS: { AUTH_STATE_CHANGED, AUTH_SESSION_UPDATED },
    // One tab, so nothing hears its session notices; Node's own channel would keep the run alive.
    BroadcastChannel: class {
      postMessage = () => undefined;
      close = () => undefined;
    }
  });

  /** What the provider hands the app now. */
  const auth = () => {
    cursor = 0;
    effects = [];
    return AuthState({ children: null }).props.value;
  };

  // The first draw and its effects: the first status read says the browser holds old-session.
  auth();
  effects.forEach((effect) => effect());
  await turn();
  statusReads.shift().answer(statusAnswer('old-session'));
  await turn();
  await turn();
  dispatched.length = 0;
  return { auth, statusReads, listeners, dispatched };
};

/** Starts the 401's status read, then the sign-in's, and returns both held reads in that order. */
const startBothReads = async (tab) => {
  tab.listeners[AUTH_STATE_CHANGED]();
  await turn();
  const signInRead = tab.auth().refreshAuth();
  await turn();
  assert.equal(
    tab.statusReads.length,
    2,
    'the 401 and the sign-in did not each start a status read'
  );
  const [olderRead, newestRead] = tab.statusReads.splice(0, 2);
  return { olderRead, newestRead, signInRead };
};

const settle = async () => {
  await turn();
  await turn();
};

test('an older status read answering signed out while a newer one is out leaves the tab as it was', async () => {
  const originalError = console.error;
  console.error = () => undefined;
  try {
    const tab = await mountAuth();
    assert.equal(tab.auth().authMode, 'authenticated', 'the tab does not start signed in');
    const { olderRead, newestRead, signInRead } = await startBothReads(tab);

    olderRead.answer(statusAnswer(null));
    await settle();
    const between = tab.auth();
    assert.equal(
      between.authMode,
      'authenticated',
      `the older status read's "signed out" answer shows the tab ${between.authMode} while the newer read is still out`
    );
    assert.deepEqual(
      tab.dispatched,
      [],
      'the older status read announced a session change while the newer read is still out'
    );

    newestRead.answer(statusAnswer('new-session'));
    await signInRead;
    await settle();
    const after = tab.auth();
    assert.equal(after.authMode, 'authenticated', `the tab ends ${after.authMode}`);
    assert.equal(after.sessionId, 'new-session', 'the tab does not hold the newest answer');
    assert.equal(after.isLoading, false, 'the newest answer did not end the loading state');
    assert.deepEqual(
      tab.dispatched,
      [AUTH_SESSION_UPDATED],
      'the newest answer did not announce the session change once'
    );
  } finally {
    console.error = originalError;
    delete globalThis.window;
    delete globalThis.Event;
  }
});

test('when the newest status read gets no answer, the older answer it held back is shown', async () => {
  const originalError = console.error;
  console.error = () => undefined;
  try {
    const tab = await mountAuth();
    const { olderRead, newestRead, signInRead } = await startBothReads(tab);

    olderRead.answer(statusAnswer(null));
    await settle();
    newestRead.answer(unanswered);
    await signInRead;
    await settle();
    const after = tab.auth();
    assert.equal(
      after.authMode,
      'unauthenticated',
      `the server last said signed out and the newest read got no answer, but the tab is left ${after.authMode}`
    );
    assert.equal(after.sessionId, null, 'the tab still holds the old session');
  } finally {
    console.error = originalError;
    delete globalThis.window;
    delete globalThis.Event;
  }
});
