import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import React from 'react';
import { compileToUrl, make, moduleUrl, parseSource } from './transpile-module.mjs';

/**
 * A sign-in in this tab can end the session the browser held, so a request still on the wire with
 * the old cookie answers 401, and that 401 starts a status read. Whether that read can answer
 * "signed out" after the sign-in depends on the order the auth service sends requests in, so the
 * shipped service runs here with the shipped auth provider:
 *
 * - The password and key sign-in share one queue with the status reads (`serialize` in
 *   auth.service.ts), so a read the 401 starts while the sign-in is out is sent after its answer,
 *   with the new cookie.
 * - The main administrator recovery posts outside that queue (AdminAccountStep.tsx) and reads the
 *   status afterwards. A read the 401 sends before the recovery answers carries the old cookie and
 *   answers "signed out"; it is the older read, so it answers first.
 *
 * The first-admin post sets no cookie and ends no session, so it starts no such read.
 *
 * Every request is held until the test answers it; each one is answered for the cookie it was
 * sent with, as the server does.
 */

const AUTH_CONTEXT = 'src/contexts/AuthContext.tsx';
const AUTH_STATE_CHANGED = 'auth-state-changed';
const OLD_SESSION = 'old-session';
const NEW_SESSION = 'new-session';

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

/** The shipped auth service, with only its browser and translation edges replaced. */
const { default: authService, isAccountHolder } = await import(
  await compileToUrl('../src/services/auth.service.ts', {
    '@/i18n': moduleUrl('export default { t: (key) => key };'),
    '@utils/constants': moduleUrl(
      "export const getApiUrl = () => ''; export const APP_EVENTS = { SHOW_TOAST: 'show-toast' };"
    ),
    '@utils/antiforgery': moduleUrl('export const antiforgeryHeaders = () => ({});'),
    '@utils/error': moduleUrl("export const isAbortError = (e) => e?.name === 'AbortError';"),
    '@utils/userInteractionTracker': moduleUrl(
      'export const hasRecentUserInteraction = () => false;'
    ),
    './apiError': moduleUrl(
      'export class ApiError extends Error {} export const assertOk = async (response) => response;'
    )
  })
);

const turn = () => new Promise((resolve) => setImmediate(resolve));
const settle = async () => {
  for (let step = 0; step < 6; step += 1) await turn();
};

/** The status answer for a request sent with a cookie naming `sessionId`, or none when it is null. */
const statusAnswer = (sessionId) => ({
  isAuthenticated: sessionId !== null,
  authenticationEnabled: true,
  accountMode: 'apiKeyPassword',
  authenticationSetupRequired: false,
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
  incompleteLoginServices: [],
  guestAccessEnabled: true,
  guestDurationHours: 6
});

/** One browser: its cookie, the server's live sessions, and every request sent and not answered. */
const openBrowser = () => {
  const browser = { cookie: OLD_SESSION, live: new Set([OLD_SESSION]), waiting: [] };
  globalThis.fetch = (url) =>
    new Promise((resolve) => {
      browser.waiting.push({
        path: new URL(String(url), 'http://lancache.test').pathname,
        // The browser attaches the cookie it holds when the request is sent.
        cookie: browser.cookie,
        resolve
      });
    });
  /** The one request waiting for `path`; fails when there is none or more than one. */
  browser.take = (path) => {
    const matching = browser.waiting.filter((request) => request.path === path);
    assert.equal(matching.length, 1, `expected one ${path} request on the wire`);
    browser.waiting.splice(browser.waiting.indexOf(matching[0]), 1);
    return matching[0];
  };
  browser.statusReadsOut = () =>
    browser.waiting.filter((request) => request.path === '/api/auth/status').length;
  browser.answerStatus = (request) =>
    request.resolve({
      ok: true,
      status: 200,
      json: async () => statusAnswer(browser.live.has(request.cookie) ? request.cookie : null)
    });
  /** The server runs the sign-in: the new session exists and the old one has ended. */
  browser.serverSignsIn = () => {
    browser.live.delete(OLD_SESSION);
    browser.live.add(NEW_SESSION);
  };
  /** The sign-in's answer lands and the browser stores the new cookie. */
  browser.answerSignIn = (request) => {
    browser.cookie = NEW_SESSION;
    request.resolve({
      ok: true,
      status: 200,
      json: async () => ({ success: true, sessionType: 'admin' })
    });
  };
  return browser;
};

/** Runs the provider on the shipped service; its first status read finds old-session. */
const mountAuth = async (browser) => {
  const listeners = {};
  globalThis.window = {
    addEventListener: (name, handler) => {
      listeners[name] = handler;
    },
    removeEventListener: () => undefined,
    dispatchEvent: () => true
  };
  globalThis.Event = class {
    constructor(type) {
      this.type = type;
    }
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
    isAccountHolder,
    useSignalR: () => ({
      on: () => undefined,
      off: () => undefined,
      invoke: async () => undefined,
      isConnected: false
    }),
    AuthContext: { Provider: stub('AuthContext') },
    APP_EVENTS: { AUTH_STATE_CHANGED, AUTH_SESSION_UPDATED: 'auth-session-updated' },
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

  auth();
  effects.forEach((effect) => effect());
  await settle();
  browser.answerStatus(browser.take('/api/auth/status'));
  await settle();
  return { auth, listeners };
};

/** A request this tab sent with the old cookie reached the server after the sign-in and got 401. */
const unauthorizedAnswer = (tab) => tab.listeners[AUTH_STATE_CHANGED]();

const quietErrors = async (body) => {
  const originalError = console.error;
  console.error = () => undefined;
  try {
    await body();
  } finally {
    console.error = originalError;
    delete globalThis.window;
    delete globalThis.Event;
    delete globalThis.fetch;
  }
};

for (const answered of ['while the sign-in is on the wire', 'after the sign-in answered']) {
  test(`a 401 status read that starts ${answered} is sent with the new cookie and the tab stays signed in`, () =>
    quietErrors(async () => {
      const browser = openBrowser();
      const tab = await mountAuth(browser);
      assert.equal(tab.auth().authMode, 'authenticated', 'the tab does not start signed in');

      const signingIn = tab.auth().login('key', 'colleague', 'Correct-Horse-9');
      await settle();
      // The sign-in reads the status first, inside the queue, while the old session is live.
      browser.answerStatus(browser.take('/api/auth/status'));
      await settle();
      const signIn = browser.take('/api/auth/login');
      browser.serverSignsIn();

      if (answered === 'while the sign-in is on the wire') {
        unauthorizedAnswer(tab);
        await settle();
        assert.equal(
          browser.statusReadsOut(),
          0,
          'the 401 status read was sent while the sign-in was out, so it carries the old cookie'
        );
      }
      browser.answerSignIn(signIn);
      await settle();
      if (answered === 'after the sign-in answered') {
        unauthorizedAnswer(tab);
        await settle();
      }

      while (browser.statusReadsOut() > 0) {
        const read = browser.take('/api/auth/status');
        assert.equal(
          read.cookie,
          NEW_SESSION,
          'a status read after the sign-in was sent with the old cookie, so it answers "signed out"'
        );
        browser.answerStatus(read);
        await settle();
      }
      await signingIn;
      await settle();

      const after = tab.auth();
      assert.equal(after.authMode, 'authenticated', `the tab is left ${after.authMode}`);
      assert.equal(after.sessionId, NEW_SESSION, 'the tab does not hold the new session');
    }));
}

for (const landed of ['before the recovery answers', 'after the recovery answered']) {
  test(`after a main administrator recovery, a 401 status read sent with the old cookie that answers ${landed} leaves the tab on the new session`, () =>
    quietErrors(async () => {
      const browser = openBrowser();
      const tab = await mountAuth(browser);

      // The recovery form posts with a bare fetch, outside the auth queue.
      const recovering = globalThis.fetch('/api/account-setup/recover-main-admin', {
        method: 'POST'
      });
      const recovery = browser.take('/api/account-setup/recover-main-admin');
      browser.serverSignsIn();
      unauthorizedAnswer(tab);
      await settle();
      const olderRead = browser.take('/api/auth/status');
      assert.equal(
        olderRead.cookie,
        OLD_SESSION,
        'the 401 status read did not carry the old cookie'
      );

      if (landed === 'before the recovery answers') {
        browser.answerStatus(olderRead);
        await settle();
        browser.answerSignIn(recovery);
        await recovering;
        const refreshing = tab.auth().refreshAuth();
        await settle();
        const newerRead = browser.take('/api/auth/status');
        assert.equal(
          newerRead.cookie,
          NEW_SESSION,
          'the recovery status read carries the old cookie'
        );
        browser.answerStatus(newerRead);
        await refreshing;
      } else {
        browser.answerSignIn(recovery);
        await recovering;
        const refreshing = tab.auth().refreshAuth();
        await settle();
        assert.equal(
          browser.statusReadsOut(),
          0,
          'the newer read was sent before the older answered'
        );
        browser.answerStatus(olderRead);
        await settle();
        const between = tab.auth();
        assert.equal(
          between.authMode,
          'authenticated',
          `the older read's "signed out" answer shows the tab ${between.authMode} while the recovery's read is still out`
        );
        const newerRead = browser.take('/api/auth/status');
        assert.equal(
          newerRead.cookie,
          NEW_SESSION,
          'the recovery status read carries the old cookie'
        );
        browser.answerStatus(newerRead);
        await refreshing;
      }
      await settle();

      const after = tab.auth();
      assert.equal(after.authMode, 'authenticated', `the tab is left ${after.authMode}`);
      assert.equal(after.sessionId, NEW_SESSION, 'the tab does not hold the new session');
    }));
}
