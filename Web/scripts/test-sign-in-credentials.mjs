import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';

/**
 * The sign-in screen now takes three credentials instead of one, and the guest button beside it still
 * takes none. Both facts live in one component and neither has a runtime harness, so they are read out
 * of the product source: which arguments reach the login call, which inputs the form draws, and which
 * of them the guest path touches.
 *
 * The rotation notice is checked the same way. Rotating the API key ends every session at once, so the
 * people it signs out all arrive at this screen together and read a credential refusal as a wrong
 * password. The server answers every sign-in failure with one message on purpose, so the notice is the
 * only place the key gets named.
 */

const readWebSource = (relativePath) =>
  readFileSync(new URL(`../${relativePath}`, import.meta.url), 'utf8');

const parse = (fileName, source) =>
  ts.createSourceFile(fileName, source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);

const modalSource = readWebSource('src/components/modals/auth/AuthenticationModal.tsx');
const accountStepSource = readWebSource('src/components/initialization/steps/AdminAccountStep.tsx');
const credentialFieldsSource = readWebSource('src/components/ui/CredentialFields.tsx');

const modalFile = parse('AuthenticationModal.tsx', modalSource);
const accountStepFile = parse('AdminAccountStep.tsx', accountStepSource);
const credentialFieldsFile = parse('CredentialFields.tsx', credentialFieldsSource);

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

/** The right-hand side of every `<guard> && <jsx>` written with the given guard, as source text. */
const guardedBy = (sourceFile, guard) =>
  collect(
    sourceFile,
    (node) =>
      ts.isBinaryExpression(node) &&
      node.operatorToken.kind === ts.SyntaxKind.AmpersandAmpersandToken &&
      node.left.getText(sourceFile) === guard
  ).map((node) => node.right.getText(sourceFile));

/** Every key passed to a `t('...')` call inside a fragment of source. */
const translationKeysIn = (fragment) =>
  [...fragment.matchAll(/\bt\('([^']+)'/g)].map((match) => match[1]);

const loginCalls = collect(
  modalFile,
  (node) => ts.isCallExpression(node) && node.expression.getText(modalFile) === 'authLogin'
);

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

// The three fields are drawn once, in CredentialFields, and the modal renders that. They used to be
// written out here, and the copies drifted: this screen rendered the installation's API key as plain
// text while the two settings screens masked it.
const credentialInputs = attributesOf(credentialFieldsFile, 'input');
const renderedBlocks = attributesOf(modalFile, 'CredentialFields');

const guestHandler = initializerOf(modalFile, 'handleStartGuestMode');
const signInHandler = initializerOf(modalFile, 'handleAuthenticate');

test('signing in sends the API key, a username and a password', () => {
  assert.equal(loginCalls.length, 1, 'expected exactly one login call in the sign-in modal');
  assert.deepEqual(
    loginCalls[0].arguments.map((argument) => argument.getText(modalFile)),
    ['apiKey', 'username.trim()', 'password']
  );
});

test('the API key field is still on the form beside the two new ones', () => {
  assert.equal(
    renderedBlocks.length,
    1,
    'expected exactly one credential block on the sign-in form'
  );
  assert.deepEqual(
    [renderedBlocks[0].apiKey, renderedBlocks[0].username, renderedBlocks[0].password],
    ['{apiKey}', '{username}', '{password}']
  );
  assert.deepEqual(
    credentialInputs.map((input) => input.value),
    ['{apiKey}', '{username}', '{password}']
  );
  assert.ok(
    credentialFieldsSource.includes("t('modals.auth.labels.apiKey')"),
    'the API key field lost its label'
  );
});

test('the API key is masked like the password it is', () => {
  assert.deepEqual(
    credentialInputs.map((input) => input.type),
    ['"password"', '"text"', '"password"']
  );
});

test('continuing as a guest asks for nothing', () => {
  for (const credential of ['apiKey', 'username', 'password']) {
    assert.ok(
      !guestHandler.includes(credential),
      `the guest path reads ${credential}, so it no longer works without credentials`
    );
  }

  const guestButtons = collect(
    modalFile,
    (node) =>
      ts.isJsxElement(node) &&
      node.openingElement.getText(modalFile).includes('onClick={handleStartGuestMode}')
  ).map((node) => node.getText(modalFile));
  assert.equal(guestButtons.length, 1, 'expected exactly one guest button');
  for (const credential of ['apiKey', 'username', 'password', 'credentialsFilled']) {
    assert.ok(!guestButtons[0].includes(credential), `the guest button is gated on ${credential}`);
  }
});

test('a refused sign-in names the installation key in copy of its own', () => {
  const notices = guardedBy(modalFile, 'signInRefused && requiresApiKey(accountMode)');
  assert.equal(notices.length, 1, 'expected exactly one rotation notice');

  const noticeKeys = translationKeysIn(notices[0]);
  assert.ok(noticeKeys.length > 0, 'the rotation notice renders no text');

  const refusalKeys = translationKeysIn(signInHandler);
  for (const key of noticeKeys) {
    assert.ok(
      !refusalKeys.includes(key),
      `${key} is also the wrong-credentials message, so the two read the same`
    );
  }
});

test('only a refused sign-in raises the rotation notice', () => {
  assert.ok(
    signInHandler.includes('setSignInRefused(true)'),
    'a refused sign-in leaves the notice hidden'
  );
  assert.ok(
    !guestHandler.includes('setSignInRefused'),
    'a guest-mode failure raises the rotation notice too'
  );
});

test('the sign-in notices are the shared Alert, and the refusal is announced', () => {
  // A guarded fragment is written `guard && (<jsx>)`, so the element starts after the paren.
  const elementOf = (fragment) => fragment.replace(/^\(\s*/, '');
  const [rotation] = guardedBy(modalFile, 'signInRefused && requiresApiKey(accountMode)');
  assert.ok(
    elementOf(rotation).startsWith('<Alert color="warning"'),
    'the rotation notice is hand-drawn'
  );

  const refusals = guardedBy(modalFile, 'authError');
  assert.equal(refusals.length, 1, 'expected exactly one refusal notice');
  assert.ok(refusals[0].includes('role="alert"'), 'the refusal is not announced');
  assert.ok(refusals[0].includes('<Alert color="error">'), 'the refusal notice is hand-drawn');

  const help = guardedBy(modalFile, 'requiresApiKey(accountMode)');
  assert.ok(
    help.some((fragment) => elementOf(fragment).startsWith('<Alert color="info"')),
    'the API key help is hand-drawn'
  );
});

test('creating the first account warns when the page is not served over HTTPS', () => {
  const warnings = guardedBy(accountStepFile, '!window.isSecureContext');
  assert.equal(warnings.length, 1, 'expected exactly one insecure-connection warning');

  const warningKeys = translationKeysIn(warnings[0]);
  assert.deepEqual(warningKeys, [
    'initialization.adminAccount.insecureConnection.title',
    'initialization.adminAccount.insecureConnection.description'
  ]);
});

test('a failed external sign-in is worded from the bounded categories, not the query string', () => {
  const [, initial] =
    /\[authError, setAuthError\] = useState<string \| null>\(\(\) => \{([\s\S]*?)\}\);/.exec(
      modalSource
    ) ?? [];
  assert.ok(initial, 'the initial sign-in error is not read from the address bar');
  assert.ok(initial.includes("get('oidcError')"), 'the failure marker is not read');
  assert.ok(initial.includes('t(loginErrorKey(code))'), 'the marker bypasses the bounded table');
  assert.ok(!initial.includes('t(code)'), 'the raw marker is used as a translation key');
  assert.ok(!initial.includes('accessSetup.errors.${'), 'the raw marker is spliced into a key');
});
