import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import typescript from 'typescript';
import { bindLifted, findSoleNode, parseSource } from './transpile-module.mjs';

/**
 * With sign-in off no account exists: the server refuses Create account, so the Users list does not
 * draw it. The access settings let a browser on the shared session switch to unauthenticated access,
 * which deletes any account left from an earlier mode, without the owner sign-in step: with sign-in off
 * every browser already has full admin, and the server skips the owner check for that switch only.
 */

const USER_TAB = 'src/components/features/user/UserTab.tsx';
const ACCESS_SETUP = 'src/components/initialization/AccessSetup.tsx';

/** Draws the button with its label, so the markup says whether Create account is there. */
const Button = ({ children }) => React.createElement('button', null, children);

/**
 * The shipped list foot of the Users page, with whatever condition the page draws it under, rendered
 * for a browser whose sign-in state is `authenticationEnabled`, signed in as an admin.
 */
const renderListFoot = (authenticationEnabled) => {
  const source = parseSource(USER_TAB);
  const foot = findSoleNode(
    source,
    'users-list-foot element',
    (node) =>
      typescript.isJsxElement(node) &&
      node.openingElement.attributes.properties.some(
        (attribute) =>
          typescript.isJsxAttribute(attribute) &&
          attribute.name.getText(source) === 'className' &&
          attribute.initializer?.getText(source) === '"users-list-foot"'
      )
  );
  // Climb to the whole `{...}` the foot sits in, so the condition it is drawn under comes with it.
  let drawn = foot;
  while (
    drawn.parent &&
    (typescript.isParenthesizedExpression(drawn.parent) ||
      typescript.isBinaryExpression(drawn.parent))
  ) {
    drawn = drawn.parent;
  }
  const value = bindLifted(
    `() => (${drawn.getText(source)})`,
    {
      React,
      authenticationEnabled,
      sessionType: 'admin',
      Button,
      t: (key) => key,
      setEditor: () => undefined
    },
    { jsx: typescript.JsxEmit.React }
  )();
  return renderToStaticMarkup(React.createElement(React.Fragment, null, value));
};

/** The shipped rule for whether the access settings ask for the owner sign-in first. */
const needsSignIn = (state) => {
  const source = parseSource(ACCESS_SETUP);
  const declaration = findSoleNode(
    source,
    'needsSignIn declaration',
    (node) => typescript.isVariableDeclaration(node) && node.name.getText(source) === 'needsSignIn'
  );
  return bindLifted(`() => (${declaration.initializer.getText(source)})`, state)();
};

test('the Users list draws Create account only while sign-in is on', () => {
  assert.match(renderListFoot(true), /user\.accounts\.actions\.create/);
  assert.doesNotMatch(
    renderListFoot(false),
    /user\.accounts\.actions\.create/,
    'Create account is drawn with sign-in off, where the server refuses it'
  );
});

test('with sign-in off, switching to unauthenticated access asks for no owner sign-in', () => {
  const leftover = { setupStatus: { accountExists: true }, isMainAdmin: false };
  assert.equal(
    needsSignIn({ ...leftover, authenticationEnabled: false, mode: 'unauthenticated' }),
    false,
    'the shared session is asked to sign in as an owner that does not exist before it can delete the leftover account'
  );
  assert.equal(
    needsSignIn({ ...leftover, authenticationEnabled: false, mode: 'apiKeyPassword' }),
    true,
    'turning sign-in on over a leftover account skipped the owner step'
  );
  assert.equal(
    needsSignIn({ ...leftover, authenticationEnabled: true, mode: 'unauthenticated' }),
    true,
    'turning sign-in off while it is on skipped the owner step'
  );
  assert.equal(
    needsSignIn({
      setupStatus: { accountExists: true },
      isMainAdmin: true,
      authenticationEnabled: true,
      mode: 'unauthenticated'
    }),
    false
  );
});
