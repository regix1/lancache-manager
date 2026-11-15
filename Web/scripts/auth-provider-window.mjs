import { readFileSync } from 'node:fs';
import React from 'react';
import { bareAnswer, loadAuthService, noAnswer } from './sign-out-auth-service.mjs';
import { bindLifted, importsOf, liftConstArrow, make } from './transpile-module.mjs';

/**
 * One browser window running the shipped auth provider on a small hook store that keeps effect and
 * callback dependencies, so refs follow the state the way they do in the browser. The hook store
 * follows the one in `test-auth-newest-status-read-wins.mjs`. The provider drives the shipped auth
 * service over a stubbed `fetch`: every status read is held until the test answers it, so a test can
 * read what the window shows before the answer lands, and the sign-out gets the answer the test
 * mounted the window with. The window's events reach their listeners, among them the shipped
 * session-update handler of the live connection (`SignalRContext/index.tsx`) on a fake socket, and
 * the toasts and session notices the window sends are recorded.
 */

const AUTH_CONTEXT = 'src/contexts/AuthContext.tsx';
const SIGNALR_CONTEXT = 'src/contexts/SignalRContext/index.tsx';
const AUTH_STATE_CHANGED = 'auth-state-changed';
const AUTH_SESSION_UPDATED = 'auth-session-updated';
const SHOW_TOAST = 'show-toast';

const englishTexts = JSON.parse(
  readFileSync(new URL('../src/i18n/locales/en.json', import.meta.url), 'utf8')
);

/**
 * The error toast the sign-out raises with the shipped English text of `key`.
 *
 * @param {string} key The `auth.errors` key, e.g. `logoutUnconfirmed`.
 * @returns {{ type: string, message: string }} The toast detail.
 */
export const signOutNotice = (key) => ({ type: 'error', message: englishTexts.auth.errors[key] });

/** Draws nothing; the test reads the props it was given from the element tree. */
const stub = (name) => {
  const component = () => null;
  Object.defineProperty(component, 'name', { value: name });
  return component;
};

/** Every name the provider imports, so a stub stands in for any the test does not bind itself. */
const importedNames = importsOf(AUTH_CONTEXT).map(({ local }) => local);

const turn = () => new Promise((resolve) => setImmediate(resolve));

/**
 * The status answer for an admin signed in on `sessionId`.
 *
 * @param {string} sessionId The session the cookie names.
 * @param {boolean} authenticationEnabled False for an install running with authentication off.
 * @returns {Record<string, unknown>} The answer `authService.checkAuth` resolves.
 */
export const signedInAnswer = (sessionId, authenticationEnabled) => ({
  reachable: true,
  isAuthenticated: true,
  authenticationEnabled,
  accountMode: authenticationEnabled ? 'apiKeyPassword' : 'unauthenticated',
  sessionType: 'admin',
  sessionId,
  accountId: authenticationEnabled ? `account-of-${sessionId}` : null,
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

/** Whether a dependency list changed since the last render. */
const changed = (previous, next) =>
  !previous || !next || next.some((value, index) => !Object.is(value, previous[index]));

const consoleError = console.error;
const consoleWarn = console.warn;

/**
 * Mounts the provider and answers its first status read with `firstAnswer`.
 *
 * @param {Record<string, unknown>} firstAnswer The answer to the first status read.
 * @param {number | null} signOutStatus The HTTP status the sign-out is answered with, or null for a
 *   sign-out that gets no answer at all.
 * @returns {Promise<{ auth: () => Record<string, unknown>, statusReads: Function[], toasts: object[],
 *   posted: unknown[], signalRHandlers: Map<string, Function>, socketOpen: () => boolean }>} Draws the
 *   provider and returns what it hands the app; the status reads still held, each answered with a
 *   status answer or null for no answer; the toasts raised and the session notices posted so far; the
 *   server event handlers the provider registered, by event name; whether the live connection is open.
 */
export const mountAuthWindow = async (firstAnswer, signOutStatus) => {
  const listeners = new Map();
  const toasts = [];
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
      if (event.type === SHOW_TOAST) toasts.push(event.detail);
      for (const listener of listeners.get(event.type) ?? []) listener(event);
      return true;
    }
  };
  globalThis.Event = class {
    constructor(type) {
      this.type = type;
    }
  };
  // A refused sign-out and an unanswered read log on purpose.
  console.error = () => undefined;
  console.warn = () => undefined;

  const statusReads = [];
  globalThis.fetch = async (url) => {
    if (url.endsWith('/api/auth/logout'))
      return signOutStatus === null ? noAnswer() : bareAnswer(signOutStatus);
    return new Promise((resolve) => {
      statusReads.push((answer) =>
        resolve(
          answer === null
            ? Promise.resolve().then(noAnswer)
            : { ...bareAnswer(200), json: async () => answer }
        )
      );
    });
  };
  const authService = await loadAuthService('en');

  // The live connection: the shipped session-update handler on a socket that opens when started.
  const connectionRef = { current: null };
  const handleAuthSessionUpdated = bindLifted(
    liftConstArrow(SIGNALR_CONTEXT, 'handleAuthSessionUpdated'),
    {
      authService,
      reconnectTimeoutRef: { current: null },
      connectionRef,
      isMountedRef: { current: true },
      isPageVisibleRef: { current: true },
      setConnectionState: () => undefined,
      setIsConnected: () => undefined,
      setConnectionId: () => undefined,
      setupConnection: () => {
        connectionRef.current ??= { stop: async () => undefined };
      }
    }
  );
  globalThis.window.addEventListener(AUTH_SESSION_UPDATED, handleAuthSessionUpdated);

  const posted = [];
  const signalRHandlers = new Map();
  const signalR = {
    on: (eventName, handler) => {
      signalRHandlers.set(eventName, handler);
    },
    off: (eventName, handler) => {
      if (signalRHandlers.get(eventName) === handler) signalRHandlers.delete(eventName);
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
    isAccountHolder: (sessionType) => sessionType === 'admin' || sessionType === 'user',
    useSignalR: () => signalR,
    AuthContext: { Provider: stub('AuthContext') },
    APP_EVENTS: { AUTH_STATE_CHANGED, AUTH_SESSION_UPDATED },
    // One window, so nothing hears its session notices, which are recorded instead; Node's own
    // channel would keep the run alive.
    BroadcastChannel: class {
      postMessage = (message) => {
        posted.push(message);
      };
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

  auth();
  await turn();
  statusReads.shift()(firstAnswer);
  await turn();
  await turn();
  return {
    auth,
    statusReads,
    toasts,
    posted,
    signalRHandlers,
    socketOpen: () => connectionRef.current !== null
  };
};

/**
 * Clicks Log Out in Settings, which signs out and then reads the status again
 * (`AuthenticationManager.tsx:285-286`). Every status read the provider starts, inside the sign-out
 * or after it, is answered with `readAnswer`, and only after the auth mode the window shows has been
 * read.
 *
 * @param {{ auth: () => Record<string, unknown>, statusReads: Function[] }} tab The mounted window.
 * @param {Record<string, unknown> | null} readAnswer The answer to every status read from here on,
 *   or null for reads that get no answer.
 * @returns {Promise<string[]>} The auth mode the window showed at each step, the last one at the end.
 */
export const logOutAndWatch = async (tab, readAnswer) => {
  let done = false;
  const click = (async () => {
    await tab.auth().logout();
    await tab.auth().refreshAuth();
  })().finally(() => {
    done = true;
  });

  const shown = [];
  for (let step = 0; !done && step < 200; step += 1) {
    await turn();
    shown.push(tab.auth().authMode);
    while (tab.statusReads.length > 0) tab.statusReads.shift()(readAnswer);
  }
  await click;
  shown.push(tab.auth().authMode);
  return shown;
};

/** Removes the browser globals `mountAuthWindow` set and gives the console back. */
export const unmountAuthWindow = () => {
  delete globalThis.window;
  delete globalThis.Event;
  delete globalThis.fetch;
  console.error = consoleError;
  console.warn = consoleWarn;
};
