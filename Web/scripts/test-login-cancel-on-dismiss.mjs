import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';

/**
 * Closing a login modal has to stop the sign-in on the server, and finishing one must not. The
 * server-side poll deliberately ignores the browser's abort signal - a tab that goes away must never
 * throw out a confirmation the user already approved on their phone - so the dismiss handler is the
 * only thing left that can end it. These modals have no runtime harness, so the two paths are read
 * out of the product source: which close handler reaches onCancelLogin, and which does not.
 */

const readWebSource = (relativePath) =>
  readFileSync(new URL(`../${relativePath}`, import.meta.url), 'utf8');

const parse = (fileName, source) =>
  ts.createSourceFile(fileName, source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);

const steamModalSource = readWebSource('src/components/modals/auth/SteamAuthModal.tsx');
const steamManagerSource = readWebSource(
  'src/components/features/management/steam/SteamLoginManager.tsx'
);
const xboxStatusSource = readWebSource(
  'src/components/features/management/xbox/XboxDaemonStatus.tsx'
);

const steamModalFile = parse('SteamAuthModal.tsx', steamModalSource);
const steamManagerFile = parse('SteamLoginManager.tsx', steamManagerSource);
const xboxStatusFile = parse('XboxDaemonStatus.tsx', xboxStatusSource);

const collect = (sourceFile, matches) => {
  const found = [];
  const visit = (node) => {
    if (matches(node)) {
      found.push(node);
    }
    ts.forEachChild(node, visit);
  };
  ts.forEachChild(sourceFile, visit);
  return found;
};

/** The initializer of a named `const`, as source text. */
const initializerOf = (sourceFile, name) => {
  const declarations = collect(
    sourceFile,
    (node) => ts.isVariableDeclaration(node) && node.name.getText(sourceFile) === name
  );
  assert.equal(declarations.length, 1, `expected exactly one ${name} declaration`);
  assert.ok(declarations[0].initializer, `${name} has no initializer`);
  return declarations[0].initializer.getText(sourceFile);
};

/** Every JSX element with the given tag, as a map of attribute name to source text. */
const attributesOf = (sourceFile, tagName) =>
  collect(
    sourceFile,
    (node) =>
      (ts.isJsxSelfClosingElement(node) || ts.isJsxOpeningElement(node)) &&
      node.tagName.getText(sourceFile) === tagName
  ).map((node) =>
    Object.fromEntries(
      node.attributes.properties
        .filter((attribute) => ts.isJsxAttribute(attribute))
        .map((attribute) => [
          attribute.name.getText(sourceFile),
          attribute.initializer?.getText(sourceFile) ?? null
        ])
    )
  );

const closeHandler = initializerOf(steamModalFile, 'handleCloseModal');
const submitHandler = initializerOf(steamModalFile, 'handleSubmit');

test('dismissing the Steam modal during the phone wait cancels the sign-in', () => {
  // The branch that runs while the poll is still waiting on a phone tap. It used to call
  // onCancelLogin only in prefill mode, which left the manager's own poll asking Steam for minutes
  // after the modal was gone.
  const phoneWaitBranch = closeHandler.slice(
    closeHandler.indexOf('if (waitingForMobileConfirmation)'),
    closeHandler.indexOf('if (isPrefillMode')
  );
  assert.ok(phoneWaitBranch.length > 0, 'the phone-approval branch is no longer recognizable');
  assert.ok(
    phoneWaitBranch.includes('onCancelLogin?.()'),
    'closing during the phone wait no longer cancels the sign-in'
  );
  assert.ok(
    !phoneWaitBranch.includes('isPrefillMode'),
    'the cancel is gated on prefill mode again, so the manager flow stops cancelling'
  );
});

test('a sign-in that succeeded is never cancelled on its way out', () => {
  // Success closes through the onClose prop directly. Routing it through handleCloseModal instead
  // would cancel the very sign-in that just worked.
  assert.ok(
    !submitHandler.includes('onCancelLogin'),
    'the submit path cancels the login it just completed'
  );
  assert.ok(
    !submitHandler.includes('handleCloseModal'),
    'success now closes through the dismiss handler, which cancels the sign-in'
  );
});

test('the Steam and Xbox pages both hand their modal a server-side cancel', () => {
  const steamModals = attributesOf(steamManagerFile, 'SteamAuthModal');
  assert.equal(steamModals.length, 1, 'expected exactly one Steam login modal on the page');
  assert.equal(steamModals[0].onCancelLogin, '{handleCancelLogin}');
  assert.ok(
    initializerOf(steamManagerFile, 'handleCancelLogin').includes('ApiService.cancelSteamLogin()'),
    'the Steam cancel no longer reaches the server'
  );

  const xboxModals = attributesOf(xboxStatusFile, 'XboxMappingLoginModal');
  assert.equal(xboxModals.length, 1, 'expected exactly one Xbox login modal on the page');
  assert.equal(xboxModals[0].onCancelLogin, '{cancelLogin}');
});
