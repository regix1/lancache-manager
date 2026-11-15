import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import React from 'react';
import {
  mountAuthWindow,
  signOutNotice,
  signedInAnswer,
  unmountAuthWindow
} from './auth-provider-window.mjs';
import { make, parseSource } from './transpile-module.mjs';

/**
 * With authentication on, the owner deletes every account from the Sign-in pane. The delete ends
 * every session, the owner's own included (`AccountsController.cs:454`, `SessionService.cs:874`),
 * so the sign-out the pane used to send next finds no session and the antiforgery check refuses it
 * with a 400. That refusal raises "The sign-out did not finish" just before the page reloads, and
 * the toast is false: after the reload the window is signed out. A delete that succeeded must reload
 * the page with no refused sign-out and no sign-out toast.
 *
 * The shipped pane runs on a small hook store, following the one in
 * `test-sign-in-wizard-closes-with-fade.mjs`, over the shipped auth provider and auth service from
 * `auth-provider-window.mjs`, so the pane's sign-out reaches the real service and its toasts. The
 * server answers the delete 200 and a sign-out 400, as it does after the delete.
 */

const COMPONENT = 'src/components/features/user/SignInPane.tsx';
const WIPE_URL = '/api/accounts/wipe';
const LOGOUT_URL = '/api/auth/logout';

/** Draws nothing; the test reads the props it was given from the element tree. */
const stub = (name) => {
  const component = () => null;
  Object.defineProperty(component, 'name', { value: name });
  return component;
};

/** Every name the pane imports, so a stub stands in for any the test does not bind itself. */
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

const ConfirmationModal = stub('ConfirmationModal');

/**
 * The shipped pane on its own hook store, reading the provider through `auth`.
 *
 * @param {() => Record<string, unknown>} auth What the provider hands the app now.
 * @returns {{ wipeDialog: () => Record<string, unknown> }} The props of the delete-every-account
 *   dialog as the pane draws it now.
 */
const mountPane = (auth) => {
  const slots = [];
  let cursor = 0;
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
    }
  };
  const Pane = make(COMPONENT, 'SignInPane', {
    ...Object.fromEntries(importedNames.map((name) => [name, stub(name)])),
    React: { ...React, ...hooks },
    ...hooks,
    ConfirmationModal,
    useTranslation: () => ({ t: (key) => key }),
    useErrorHandler: () => ({ notifyError: () => undefined }),
    useHeldValue: (value) => value,
    useAuth: auth,
    usesOidc: () => false,
    getErrorMessage: (error) => String(error),
    API_BASE: '/api',
    ApiService: {
      getJsonFetchOptions: (_body, init) => init,
      handleResponse: async (response) => {
        if (!response.ok) throw new Error(`answered ${response.status}`);
        return response.json();
      }
    }
  });

  const wipeDialog = () => {
    cursor = 0;
    const found = [];
    const walk = (node) => {
      if (Array.isArray(node)) {
        node.forEach(walk);
        return;
      }
      if (!React.isValidElement(node)) return;
      if (node.type === ConfirmationModal && node.props.title === 'user.accounts.confirm.wipeTitle')
        found.push(node.props);
      Object.values(node.props).forEach(walk);
    };
    walk(Pane({}));
    assert.equal(found.length, 1, 'the Sign-in pane draws no delete-every-account dialog');
    return found[0];
  };
  return { wipeDialog };
};

test('deleting every account with authentication on reloads the page without a refused sign-out toast', async () => {
  try {
    const tab = await mountAuthWindow(
      { ...signedInAnswer('owner-session', true), isMainAdmin: true },
      400
    );
    assert.equal(tab.auth().authMode, 'authenticated', 'the window does not start signed in');

    const requests = [];
    const signOutStatuses = [];
    const windowFetch = globalThis.fetch;
    globalThis.fetch = async (url, init) => {
      requests.push(url);
      if (url.endsWith(WIPE_URL))
        return {
          ok: true,
          status: 200,
          headers: new Headers(),
          json: async () => ({ message: 'Every account was deleted.' })
        };
      const answer = await windowFetch(url, init);
      if (url.endsWith(LOGOUT_URL)) signOutStatuses.push(answer.status);
      return answer;
    };
    let reloads = 0;
    globalThis.window.location = {
      reload: () => {
        reloads += 1;
      }
    };

    const pane = mountPane(() => tab.auth());
    await pane.wipeDialog().onConfirm();

    assert.ok(
      requests.some((url) => url.endsWith(WIPE_URL)),
      'confirming the dialog sent no delete-every-account request'
    );
    assert.equal(reloads, 1, `the page reloaded ${reloads} times after the delete, not once`);
    assert.deepEqual(
      tab.toasts,
      [],
      `after a delete that succeeded, the page raised ${JSON.stringify(tab.toasts)} just before it reloaded (sign-out answered ${signOutStatuses.join(', ')}); the false "${signOutNotice('logoutUnconfirmed').message}" is what the owner sees`
    );
  } finally {
    unmountAuthWindow();
  }
});
