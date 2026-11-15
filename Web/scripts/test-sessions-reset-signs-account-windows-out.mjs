import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import React from 'react';
import { signedInAnswer } from './auth-provider-window.mjs';
import { bareAnswer, loadAuthService } from './sign-out-auth-service.mjs';
import { make, parseSource } from './transpile-module.mjs';

/**
 * With sign-in on, a reset of User sessions on the Data page deletes every session row, account rows
 * included (`Api/LancacheManager/Infrastructure/Services/System/DatabaseService.cs:745-751`), and then
 * sends `UserSessionsCleared` to every window, naming the sessions it deleted. Every window that held
 * one of them now holds a cookie for a session that no longer exists, so each one has to leave the
 * signed-in screen, not only guest windows; a window that signed in after the delete keeps its new
 * session. Clearing the guests alone sends the same event marked `sessionType: 'guest'` and naming the
 * guest sessions it ended (`Api/LancacheManager/Controllers/Auth/SessionsController.cs` `ClearGuestsAsync`):
 * that one ends those guest windows only, a guest that signed in while it ran keeps its session, and
 * account windows stay signed in.
 *
 * The shipped auth provider runs on a small hook store that keeps effect and callback dependencies, so
 * refs follow the state the way they do in the browser (the store follows `mountAuthWindow` in
 * `auth-provider-window.mjs`, whose live connection drops every handler it is given). The live
 * connection here keeps its handlers, so the test can deliver the server's event, and every status
 * read the window starts after the reset is answered the way the server answers it then: signed out.
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

/** Whether a dependency list changed since the last render. */
const changed = (previous, next) =>
  !previous || !next || next.some((value, index) => !Object.is(value, previous[index]));

/** What the server answers a status read once the reset has deleted the window's session. */
const signedOutAnswer = {
  ...signedInAnswer('deleted-session', true),
  isAuthenticated: false,
  sessionType: null,
  sessionId: null,
  accountId: null
};

/**
 * One window signed in as `sessionType` on an install with sign-in on.
 *
 * @param {'admin' | 'user' | 'guest'} sessionType The kind of session the window holds.
 * @returns {Promise<{ auth: () => Record<string, unknown>, authService: Record<string, unknown>,
 *   settle: () => Promise<void>, deliver: (eventName: string, sent: object) => void,
 *   setServerAnswer: (answer: object) => void }>} Draws the window and returns what it hands the app,
 *   the auth service it reads, a wait for its pending reads, a delivery of a server event and a way to
 *   change what the server answers its status reads.
 */
const mountSignedInWindow = async (sessionType) => {
  const listeners = new Map();
  globalThis.window = {
    addEventListener: (type, listener) => {
      listeners.set(type, [...(listeners.get(type) ?? []), listener]);
    },
    removeEventListener: (type, listener) => {
      listeners.set(
        type,
        (listeners.get(type) ?? []).filter((registered) => registered !== listener)
      );
    },
    dispatchEvent: (event) => {
      for (const listener of listeners.get(event.type) ?? []) listener(event);
      return true;
    }
  };
  globalThis.Event = class {
    constructor(type) {
      this.type = type;
    }
  };

  let serverAnswer = {
    ...signedInAnswer(`${sessionType}-session`, true),
    sessionType,
    accountId: sessionType === 'guest' ? null : `account-of-${sessionType}-session`
  };
  globalThis.fetch = async () => ({ ...bareAnswer(200), json: async () => serverAnswer });
  const authService = await loadAuthService('en');

  const handlers = new Map();
  const signalR = {
    on: (eventName, handler) => {
      handlers.set(eventName, handler);
    },
    off: (eventName, handler) => {
      if (handlers.get(eventName) === handler) handlers.delete(eventName);
    },
    invoke: async () => undefined,
    isConnected: false
  };

  const slots = [];
  let cursor = 0;
  let effects = [];
  const hooks = {
    useState: (initial) => {
      const slot = (slots[cursor++] ??= {
        value: typeof initial === 'function' ? initial() : initial
      });
      return [
        slot.value,
        (next) => {
          slot.value = typeof next === 'function' ? next(slot.value) : next;
        }
      ];
    },
    useRef: (initial) => (slots[cursor++] ??= { current: initial }),
    useMemo: (build, deps) => {
      const slot = (slots[cursor++] ??= {});
      if (!('value' in slot) || changed(slot.deps, deps)) {
        slot.value = build();
        slot.deps = deps;
      }
      return slot.value;
    },
    useCallback: (callback, deps) => {
      const slot = (slots[cursor++] ??= {});
      if (!slot.callback || changed(slot.deps, deps)) {
        slot.callback = callback;
        slot.deps = deps;
      }
      return slot.callback;
    },
    useEffect: (effect, deps) => {
      const slot = (slots[cursor++] ??= {});
      if (!slot.ran || changed(slot.deps, deps)) {
        slot.ran = true;
        slot.deps = deps;
        effects.push({ slot, effect });
      }
    }
  };
  hooks.useLayoutEffect = hooks.useEffect;
  const AuthState = make(AUTH_CONTEXT, 'AuthProvider', {
    ...Object.fromEntries(importedNames.map((name) => [name, stub(name)])),
    React: { ...React, ...hooks },
    ...hooks,
    authService,
    isAccountHolder: (type) => type === 'admin' || type === 'user',
    useSignalR: () => signalR,
    AuthContext: { Provider: stub('AuthContext') },
    APP_EVENTS: { AUTH_STATE_CHANGED, AUTH_SESSION_UPDATED },
    // One window; Node's own channel would keep the run alive.
    BroadcastChannel: class {
      postMessage = () => undefined;
      close = () => undefined;
    }
  });

  const auth = () => {
    cursor = 0;
    effects = [];
    const value = AuthState({ children: null }).props.value;
    for (const { slot, effect } of effects) {
      if (typeof slot.cleanup === 'function') slot.cleanup();
      slot.cleanup = effect();
    }
    return value;
  };

  /** Lets every status read the window started answer and draws the window again after each turn. */
  const settle = async () => {
    for (let step = 0; step < 12; step += 1) {
      await turn();
      auth();
    }
  };

  auth();
  await settle();
  return {
    auth,
    authService,
    settle,
    deliver: (eventName, sent) => handlers.get(eventName)?.(sent),
    setServerAnswer: (answer) => {
      serverAnswer = answer;
    }
  };
};

/**
 * Each server send of `UserSessionsCleared`, with exactly what it sends to a window holding a session
 * of the given type, and the session types whose rows it deleted. The reset waits on each deleted
 * session's prefill stop before it sends, so a window can sign in after the delete: its session is
 * not among the deleted ones and stays.
 */
const CLEARS = [
  [
    'the User sessions reset',
    (sessionType) => ({
      stageKey: 'signalr.dbReset.sessionsCleared.logout',
      context: {},
      clearCookies: true,
      sessionIds: [`${sessionType}-session`],
      timestamp: '2026-11-14T10:00:00Z'
    }),
    ['admin', 'user', 'guest']
  ],
  [
    'the User sessions reset, for a window that signed in after its delete',
    () => ({
      stageKey: 'signalr.dbReset.sessionsCleared.logout',
      context: {},
      clearCookies: true,
      sessionIds: ['a-session-the-reset-deleted'],
      timestamp: '2026-11-14T10:00:00Z'
    }),
    []
  ],
  [
    'clearing the guests',
    () => ({ clearedCount: 1, sessionType: 'guest', sessionIds: ['guest-session'] }),
    ['guest']
  ],
  [
    'clearing the guests, for a guest window that signed in while the clear ran',
    () => ({
      clearedCount: 1,
      sessionType: 'guest',
      sessionIds: ['a-guest-session-the-clear-ended']
    }),
    []
  ]
];

test('a User sessions reset with sign-in on signs every window out, account windows included', async () => {
  const consoleError = console.error;
  const consoleWarn = console.warn;
  console.error = () => undefined;
  console.warn = () => undefined;
  const wrong = [];
  try {
    for (const [clear, sent, endedTypes] of CLEARS) {
      for (const sessionType of ['admin', 'user', 'guest']) {
        const tab = await mountSignedInWindow(sessionType);
        const before = tab.auth();
        assert.notEqual(
          before.authMode,
          'unauthenticated',
          `the ${sessionType} window does not start signed in`
        );
        assert.equal(
          before.sessionType,
          sessionType,
          `the window does not hold a ${sessionType} session`
        );

        // A window whose row the clear deleted is answered signed out from here on; the others keep
        // their session.
        const ended = endedTypes.includes(sessionType);
        if (ended) tab.setServerAnswer(signedOutAnswer);
        tab.deliver('UserSessionsCleared', sent(sessionType));
        await tab.settle();

        const after = tab.auth();
        const signedIn = after.authMode !== 'unauthenticated' || tab.authService.isAuthenticated;
        if (ended && signedIn)
          wrong.push(
            `${clear}: the ${sessionType} window still shows ${after.authMode} (auth service signed in: ${tab.authService.isAuthenticated})`
          );
        if (!ended && !signedIn)
          wrong.push(`${clear}: the ${sessionType} window, whose session was kept, signed out`);
      }
    }
  } finally {
    console.error = consoleError;
    console.warn = consoleWarn;
    delete globalThis.window;
    delete globalThis.Event;
    delete globalThis.fetch;
  }

  assert.deepEqual(
    wrong,
    [],
    `after UserSessionsCleared with sign-in on, the windows do not follow the sessions the server kept: ${wrong.join('; ')}`
  );
});
