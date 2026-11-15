import assert from 'node:assert/strict';
import test from 'node:test';
import ts from 'typescript';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { bindLifted, liftConstArrow, liftHookCallback } from './transpile-module.mjs';

/**
 * When the guest defaults fail to load, the Guest access pane shows the failure with a Retry above the
 * guest settings. Clicking Retry must leave what is on screen in place until the new answer arrives:
 * the shipped loader runs against reads the test holds open, and the shipped pane is rendered from the
 * state the loader leaves behind after each step.
 */

const USERS = 'src/components/features/user/UserTab.tsx';
const LOAD_FAILURE = 'The server could not be reached.';

/** A promise the test settles by hand, standing in for reads that are still on the wire. */
const deferred = () => {
  let resolve;
  let reject;
  const promise = new Promise((done, fail) => {
    resolve = done;
    reject = fail;
  });
  return { promise, resolve, reject };
};

/** Lets every pending `.then`/`await` continuation run. */
const settle = () => new Promise((resolve) => setImmediate(resolve));

/** The Users screen's guest-defaults state, its shipped loader, and the shipped Guest access pane. */
const guestAccessScreen = () => {
  const state = {
    guestDuration: null,
    guestModeLocked: false,
    defaultGuestTheme: 'dark-default',
    defaultGuestRefreshRate: 'STANDARD',
    guestRefreshRateLocked: true,
    availableThemes: [],
    guestDefaultsError: null,
    guestDefaultsLoaded: false,
    loadingGuestDefaults: false
  };
  const setter = (key) => (next) => {
    state[key] = typeof next === 'function' ? next(state[key]) : next;
  };
  let answers = deferred();
  /** Every read of one load waits on the same answer, which the test gives or refuses. */
  const read = (value) => answers.promise.then(() => value);
  const loadGuestDefaults = bindLifted(
    liftHookCallback(USERS, 'useCallback', 'getGuestSessionDuration'),
    {
      guestDefaultsRequestRef: { current: 0 },
      guestDefaultPushesRef: {
        current: { duration: 0, guestLock: 0, theme: 0, refreshRate: 0, refreshRateLock: 0 }
      },
      ApiService: {
        getGuestConfig: () => read({ isLocked: false }),
        getGuestSessionDuration: () => read({ durationHours: 6, source: 'ui' }),
        getGuestThemePreference: () => read({ themeId: 'dark-default' }),
        getDefaultGuestRefreshRate: () => read({ refreshRate: 'STANDARD', locked: true })
      },
      themeService: {
        loadThemes: () =>
          read({ themes: [{ meta: { id: 'dark-default', name: 'Dark' } }], loadError: null })
      },
      setGuestDuration: setter('guestDuration'),
      setGuestModeLocked: setter('guestModeLocked'),
      setDefaultGuestTheme: setter('defaultGuestTheme'),
      setDefaultGuestRefreshRate: setter('defaultGuestRefreshRate'),
      setGuestRefreshRateLocked: setter('guestRefreshRateLocked'),
      setAvailableThemes: setter('availableThemes'),
      setGuestDefaultsError: setter('guestDefaultsError'),
      setGuestDefaultsLoaded: setter('guestDefaultsLoaded'),
      setLoadingGuestDefaults: setter('loadingGuestDefaults'),
      getErrorMessage: (error) => error.message
    }
  );
  const paneSource = liftConstArrow(USERS, 'guestAccessPane');
  /** The pane's markup as the screen draws it from the current state. */
  const render = () => {
    const pane = bindLifted(
      paneSource,
      {
        React,
        t: (key) => key,
        ...state,
        loadGuestDefaults,
        ErrorBlock: ({ message, retryLabel }) =>
          React.createElement(
            'section',
            null,
            `error: ${message}`,
            React.createElement('button', null, retryLabel)
          ),
        GuestConfiguration: () => React.createElement('form', null, 'guest settings'),
        LoadingState: () => React.createElement('div', null, 'loading skeleton'),
        handleUpdateDuration: () => undefined,
        updatingDuration: false,
        updatingGuestLock: false,
        handleToggleGuestLock: () => undefined,
        sessionsState: { loaded: true, reload: async () => undefined },
        guestSessions: [],
        handleUpdateGuestTheme: () => undefined,
        updatingGuestTheme: false,
        handleUpdateGuestRefreshRate: () => undefined,
        updatingGuestRefreshRate: false,
        handleUpdateGuestRefreshRateLock: () => undefined,
        updatingGuestRefreshRateLock: false
      },
      { jsx: ts.JsxEmit.React }
    );
    return renderToStaticMarkup(React.createElement(React.Fragment, null, pane()));
  };
  /** Ends the reads now out: answered, or refused with `error`. */
  const answer = async (error = null) => {
    if (error === null) answers.resolve();
    else answers.reject(error);
    answers = deferred();
    await settle();
  };
  return { state, loadGuestDefaults, render, answer };
};

test('clicking Retry after a failed guest-defaults read keeps the pane and its error until the answer', async () => {
  const screen = guestAccessScreen();
  const firstLoad = screen.loadGuestDefaults();
  await screen.answer(new Error(LOAD_FAILURE));
  await firstLoad;
  const failed = screen.render();
  assert.ok(failed.includes(`error: ${LOAD_FAILURE}`), 'the failed read is shown');
  assert.ok(failed.includes('guest settings'), 'the guest settings show under the failure');

  // The person clicks Retry; the reads it starts are still out.
  const retry = screen.loadGuestDefaults();
  await settle();
  const waiting = screen.render();
  assert.ok(
    waiting.includes('guest settings') && !waiting.includes('loading skeleton'),
    'clicking Retry replaced the Guest access pane with a loading skeleton before the new answer arrived'
  );
  assert.ok(
    waiting.includes(`error: ${LOAD_FAILURE}`),
    'clicking Retry cleared the error before the new answer arrived'
  );

  await screen.answer();
  await retry;
  const recovered = screen.render();
  assert.ok(!recovered.includes('error:'), 'a good answer clears the error');
  assert.ok(recovered.includes('guest settings'));
});
