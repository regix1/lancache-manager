import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import typescript from 'typescript';
import {
  bindLifted,
  findSoleNode,
  liftConstArrow,
  liftHookCallback,
  parseSource
} from './transpile-module.mjs';

/**
 * A service configured through `Security__<Service>__*` variables arrives in `loginServices` with
 * its `environmentFields` filled. The setup screen has to open on that service: opening on any
 * other kind shows empty credential fields for a service nobody configured, while the one the
 * environment set is already connected.
 *
 * A service the environment set but left incomplete arrives in `incompleteLoginServices` instead.
 * The server still fills its environment-set fields on save, so the screen shows them read-only:
 * an editable copy would let the admin type a value the save then replaces.
 */

const accessSetup = 'src/components/initialization/AccessSetup.tsx';

/** The first value of the `kind` state, run with the auth fields it reads at mount. */
const initialKind = (pendingLoginKind, loginServices, incompleteLoginServices = []) =>
  bindLifted(liftHookCallback(accessSetup, 'useState', 'incompleteLoginServices'), {
    pendingLoginKind,
    loginServices,
    incompleteLoginServices
  })();

/** `setByEnvironment`, run with the connected and incomplete service for the selected kind. */
const setByEnvironment = (selectedConnected, incompleteService) =>
  bindLifted(liftConstArrow(accessSetup, 'setByEnvironment'), {
    selectedConnected,
    incompleteService
  });

const environmentOidc = {
  id: 'oidc',
  kind: 'customOidc',
  displayName: 'Authentik',
  environmentFields: ['authority', 'clientId', 'clientSecret']
};
const testedGoogle = { id: 'google', kind: 'google', displayName: 'Google', environmentFields: [] };
const incompleteGithub = {
  id: 'github',
  kind: 'github',
  displayName: 'GitHub',
  environmentFields: ['clientId']
};

test('the service picker opens on the service the environment configured', () => {
  assert.equal(initialKind(null, [testedGoogle, environmentOidc]), 'customOidc');
});

test('a connection waiting for its sign-in test still opens first', () => {
  assert.equal(initialKind('github', [environmentOidc]), 'github');
});

test('with no environment service the picker opens on Google', () => {
  assert.equal(initialKind(null, []), 'google');
  assert.equal(initialKind(null, [testedGoogle]), 'google');
});

test('the service picker opens on an environment service that is incomplete', () => {
  assert.equal(initialKind(null, [testedGoogle], [incompleteGithub]), 'github');
  assert.equal(initialKind(null, [environmentOidc], [incompleteGithub]), 'customOidc');
});

test('an incomplete environment service shows its environment-set fields read-only', () => {
  const marked = setByEnvironment(undefined, incompleteGithub);
  assert.equal(marked('clientId'), true);
  assert.equal(marked('clientSecret'), false);
});

test('a connected service still marks only its own environment fields', () => {
  const marked = setByEnvironment(environmentOidc, undefined);
  assert.equal(marked('authority'), true);
  assert.equal(marked('displayName'), false);
  assert.equal(setByEnvironment(undefined, undefined)('clientId'), false);
});

/** Whether the incomplete-environment warning renders only behind a check of `incompleteService`. */
const warningNeedsIncompleteService = (sourceFile) => {
  const warning = findSoleNode(
    sourceFile,
    'incomplete-environment warning',
    (node) =>
      typescript.isJsxElement(node) &&
      node.openingElement.tagName.getText(sourceFile) === 'Alert' &&
      node.getText(sourceFile).includes("t('accessSetup.environment.incomplete')")
  );
  // `{condition && (<Alert ...>)}`: the Alert sits in parentheses, under the `&&`.
  const condition = warning.parent.parent;
  return (
    typescript.isBinaryExpression(condition) &&
    condition.operatorToken.kind === typescript.SyntaxKind.AmpersandAmpersandToken &&
    /\bincompleteService\b/.test(condition.left.getText(sourceFile)) &&
    typescript.isJsxExpression(condition.parent)
  );
};

test('the incomplete-environment warning shows only for an incomplete service', () => {
  assert.equal(
    warningNeedsIncompleteService(parseSource(accessSetup, typescript.ScriptKind.TSX)),
    true
  );
  // The same check against a copy whose condition no longer reads the service must fail.
  const source = readFileSync(new URL(`../${accessSetup}`, import.meta.url), 'utf8');
  const condition = 'selectedConnected === undefined && incompleteService !== undefined && (';
  assert.ok(source.includes(condition));
  const unguarded = typescript.createSourceFile(
    accessSetup,
    source.replace(condition, 'false && ('),
    typescript.ScriptTarget.Latest,
    true,
    typescript.ScriptKind.TSX
  );
  assert.equal(warningNeedsIncompleteService(unguarded), false);
});
