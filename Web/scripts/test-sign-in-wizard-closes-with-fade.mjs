import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import React from 'react';
import { make, parseSource } from './transpile-module.mjs';

/**
 * The Sign-in pane's Change button opens the sign-in wizard. A dialog fades in and out: it is drawn
 * with `opened` set while it is open, and when the user closes it, it stays drawn with
 * `opened={false}` so the fade-out plays. A wizard that is simply removed on close vanishes at once.
 *
 * The whole shipped pane runs here on a small hook store; every component it draws is a stub, and
 * the test reads the props the wizard element is drawn with, so it does not care how the pane keeps
 * its open state.
 */

const COMPONENT = 'src/components/features/user/SignInPane.tsx';

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
    if (!clause) return [];
    const names = clause.name ? [clause.name.text] : [];
    const bindings = clause.namedBindings;
    if (bindings && typescript.isNamedImports(bindings))
      names.push(...bindings.elements.map((element) => element.name.text));
    return names;
  });

const AccessSetup = stub('AccessSetup');
const Button = stub('Button');

/** Runs the pane on one hook store, as the primary administrator of an installation with sign-in on. */
const mountPane = () => {
  const slots = [];
  let cursor = 0;
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
    useEffect: () => {
      cursor++;
    },
    useLayoutEffect: () => {
      cursor++;
    }
  };
  const bindings = Object.fromEntries(importedNames.map((name) => [name, stub(name)]));
  const Pane = make(COMPONENT, 'SignInPane', {
    ...bindings,
    React: { ...React, ...hooks },
    ...hooks,
    useTranslation: () => ({ t: (key) => key }),
    useErrorHandler: () => ({ notifyError: () => undefined }),
    useHeldValue: (value) => value,
    useAuth: () => ({
      isMainAdmin: true,
      authenticationEnabled: true,
      accountMode: 'password',
      signInModeFromEnvironment: false,
      loginServices: [],
      refreshAuth: async () => undefined,
      logout: async () => undefined
    }),
    usesOidc: () => false,
    getErrorMessage: (error) => String(error),
    API_BASE: '/api',
    AccessSetup,
    Button
  });

  /** The elements of `type` the pane draws now. */
  const drawn = (type) => {
    cursor = 0;
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
    walk(Pane({}));
    return found;
  };
  return { drawn };
};

test('the Sign-in wizard stays drawn with opened false when it closes, so its fade-out plays', () => {
  const pane = mountPane();
  const change = pane.drawn(Button).find((props) => props.children === 'accessSetup.change');
  assert.ok(change, 'the Sign-in pane draws no Change button');

  change.onClick();
  const whileOpen = pane.drawn(AccessSetup);
  assert.equal(whileOpen.length, 1, 'clicking Change does not draw the Sign-in wizard');
  assert.equal(
    typeof whileOpen[0].onClose,
    'function',
    'the open Sign-in wizard has no way to close'
  );

  whileOpen[0].onClose();
  const afterClose = pane.drawn(AccessSetup);
  assert.equal(
    afterClose.length,
    1,
    'closing the Sign-in wizard removes it at once, so it vanishes with no fade-out'
  );
  assert.equal(
    afterClose[0].opened,
    false,
    `the closed Sign-in wizard is drawn with opened ${String(afterClose[0].opened)}, not false`
  );
  assert.equal(
    whileOpen[0].opened,
    true,
    `the open Sign-in wizard is drawn with opened ${String(whileOpen[0].opened)}, not true`
  );
});
