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
 * With authentication on, the owner rotates the API key in Settings. The rotation ends every
 * session, the owner's own included (`ApiKeysController.cs:219`), and issues no new antiforgery
 * token, so a sign-out sent after it finds no session and the antiforgery check refuses it with a
 * 400, as it does after "Delete every account" (`SignInPane.tsx`, settle row T22). Closing the
 * new-key dialog sent that sign-out, which raised "The sign-out did not finish" over the sign-in
 * dialog it opens, and the toast is false: the window is already signed out. Closing the dialog
 * after a rotation that succeeded must open the sign-in dialog with no sign-out toast.
 *
 * The shipped Settings card runs on a small hook store, following the one in
 * `test-delete-every-account-reloads-without-refused-sign-out.mjs`, over the shipped auth provider
 * and auth service from `auth-provider-window.mjs`, so the card's sign-out reaches the real service
 * and its toasts. The server answers the rotation 200 and a sign-out 400, and every status read after
 * the rotation finds no session.
 */

const COMPONENT = 'src/components/features/management/steam/AuthenticationManager.tsx';
const ROTATE_URL = '/api/api-keys/regenerate';
const LOGOUT_URL = '/api/auth/logout';

/** Draws nothing; the test reads the props it was given from the element tree. */
const stub = (name) => {
  const component = () => null;
  Object.defineProperty(component, 'name', { value: name });
  return component;
};

/** Every name the card imports, so a stub stands in for any the test does not bind itself. */
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
const ApiKeyRotatedModal = stub('ApiKeyRotatedModal');
const Modal = stub('Modal');

const turn = () => new Promise((resolve) => setImmediate(resolve));

/** Whether a dependency list changed since the last render. */
const changed = (previous, next) =>
  !previous || !next || next.some((value, index) => !Object.is(value, previous[index]));

/**
 * The shipped card on its own hook store, reading the provider through `auth`.
 *
 * @param {() => Record<string, unknown>} auth What the provider hands the app now.
 * @returns {(type: Function) => Record<string, unknown>[]} Draws the card, runs the effects whose
 *   dependencies changed, and returns the props of every element of `type` it drew.
 */
const mountCard = (auth) => {
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
    useEffect: (effect, deps) => {
      const slot = (slots[cursor++] ??= {});
      if (!slot.ran || changed(slot.deps, deps)) {
        slot.ran = true;
        slot.deps = deps;
        effects.push({ slot, effect });
      }
    }
  };
  const Card = make(COMPONENT, 'AuthenticationManager', {
    ...Object.fromEntries(importedNames.map((name) => [name, stub(name)])),
    React: { ...React, ...hooks },
    ...hooks,
    ConfirmationModal,
    ApiKeyRotatedModal,
    Modal,
    useTranslation: () => ({ t: (key) => key }),
    useGuestConfig: () => ({ guestDurationHours: 6 }),
    useAuth: auth,
    useHeldValue: (value) => value,
    authService: {
      checkAuth: async () => ({ reachable: true, hasData: true, hasBeenInitialized: true })
    },
    requiresApiKey: () => false,
    usesOidc: () => false,
    signInServices: () => [],
    formatSessionTimeRemaining: () => null,
    getErrorMessage: (error) => String(error),
    ApiService: {
      getFetchOptions: (init) => init,
      handleResponse: async (response) => {
        if (!response.ok) throw new Error(`answered ${response.status}`);
        return response.json();
      }
    }
  });

  return (type) => {
    cursor = 0;
    effects = [];
    const tree = Card({ onError: () => undefined, onSuccess: () => undefined });
    for (const { slot, effect } of effects) {
      if (typeof slot.cleanup === 'function') slot.cleanup();
      slot.cleanup = effect();
    }
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
};

/**
 * Runs `action` while drawing the card each turn and answering every status read the provider
 * starts with `readAnswer`.
 *
 * @param {{ statusReads: Function[] }} tab The mounted window.
 * @param {(type: Function) => Record<string, unknown>[]} draw The mounted card.
 * @param {() => Promise<unknown>} action What the person does.
 * @param {Record<string, unknown>} readAnswer The answer to every status read until `action` ends.
 */
const settle = async (tab, draw, action, readAnswer) => {
  let done = false;
  const running = action().finally(() => {
    done = true;
  });
  for (let step = 0; !done && step < 200; step += 1) {
    await turn();
    draw(Modal);
    while (tab.statusReads.length > 0) tab.statusReads.shift()(readAnswer);
  }
  await running;
};

test('closing the new key after a rotation opens sign-in without a refused sign-out toast', async () => {
  try {
    const ownerAnswer = { ...signedInAnswer('owner-session', true), isMainAdmin: true };
    // Every session ended with the rotation, so the status read finds none.
    const endedAnswer = {
      ...signedInAnswer('owner-session', true),
      isAuthenticated: false,
      sessionType: null,
      sessionId: null,
      accountId: null
    };
    const tab = await mountAuthWindow(ownerAnswer, 400);
    assert.equal(tab.auth().authMode, 'authenticated', 'the window does not start signed in');

    const signOutStatuses = [];
    const windowFetch = globalThis.fetch;
    globalThis.fetch = async (url, init) => {
      if (url.endsWith(ROTATE_URL))
        return {
          ok: true,
          status: 200,
          headers: new Headers(),
          json: async () => ({ apiKey: 'lm_rotated-key' })
        };
      const answer = await windowFetch(url, init);
      if (url.endsWith(LOGOUT_URL)) signOutStatuses.push(answer.status);
      return answer;
    };

    const draw = mountCard(() => tab.auth());
    await settle(tab, draw, turn, ownerAnswer);
    for (let step = 0; draw(ApiKeyRotatedModal).length === 0 && step < 200; step += 1) {
      await turn();
      while (tab.statusReads.length > 0) tab.statusReads.shift()(ownerAnswer);
    }

    const [rotateDialog] = draw(ConfirmationModal).filter(
      (props) => props.title === 'management.auth.regenerateModal.title'
    );
    assert.ok(rotateDialog, 'the Settings card draws no rotate-key dialog');
    await settle(tab, draw, () => rotateDialog.onConfirm(), endedAnswer);

    const [keyDialog] = draw(ApiKeyRotatedModal);
    assert.ok(keyDialog, 'the Settings card draws no new-key dialog');
    assert.equal(keyDialog.opened, true, 'the rotation that succeeded showed no new key');
    assert.equal(keyDialog.apiKey, 'lm_rotated-key', 'the new-key dialog shows another key');

    await settle(tab, draw, () => keyDialog.onClose(), endedAnswer);

    assert.equal(
      draw(ApiKeyRotatedModal)[0].opened,
      false,
      'closing the new-key dialog left it open'
    );
    assert.equal(
      draw(Modal)[0].opened,
      true,
      'closing the new-key dialog after the rotation did not open the sign-in dialog'
    );
    assert.deepEqual(
      tab.toasts,
      [],
      `after a rotation that succeeded, closing the new key raised ${JSON.stringify(tab.toasts)} over the sign-in dialog (sign-out answered ${signOutStatuses.join(', ')}); the false "${signOutNotice('logoutUnconfirmed').message}" is what the owner sees`
    );
  } finally {
    unmountAuthWindow();
  }
});
