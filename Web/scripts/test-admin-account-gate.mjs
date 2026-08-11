import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import { transpile } from './transpile-module.mjs';
import { isAdminAccountRequired } from '../src/utils/adminAccountSetup.ts';

/**
 * An installation that has been running since before accounts existed reports setup as completed and
 * owns no account row, and starting the upgraded build revokes every session it had. The screens it
 * can reach are decided by three expressions in two files, and getting any one of them wrong strands
 * it on a sign-in form with nothing to sign in with.
 *
 * So the expressions are read out of the product source and run, rather than copied here: a copy
 * would keep passing after someone edited the original.
 */

const readWebSource = (relativePath) =>
  readFileSync(new URL(`../${relativePath}`, import.meta.url), 'utf8');

const appSource = readWebSource('src/App.tsx');
const hookSource = readWebSource('src/hooks/useInitializationFlow.ts');
const modalSource = readWebSource('src/components/modals/setup/DepotInitializationModal.tsx');

const parse = (fileName, source, kind) =>
  ts.createSourceFile(fileName, source, ts.ScriptTarget.Latest, true, kind);

const appFile = parse('App.tsx', appSource, ts.ScriptKind.TSX);
const hookFile = parse('useInitializationFlow.ts', hookSource, ts.ScriptKind.TS);
const modalFile = parse('DepotInitializationModal.tsx', modalSource, ts.ScriptKind.TSX);

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

const evaluate = (expression, bindings) => {
  const names = Object.keys(bindings);
  const compiled = new Function(...names, `return (${expression});`);
  return compiled(...names.map((name) => bindings[name]));
};

const wizardCondition = initializerOf(appFile, 'shouldShowInitializationFlow');

const signInConditions = collect(
  appFile,
  (node) =>
    ts.isIfStatement(node) &&
    node.expression.getText(appFile).includes("authMode === 'unauthenticated'") &&
    node.expression.getText(appFile).includes('authenticationEnabled')
);
assert.equal(signInConditions.length, 1, 'expected exactly one sign-in screen gate in App.tsx');
const signInCondition = signInConditions[0].expression.getText(appFile);

/**
 * `resolveInitialStep` and the two helpers it calls are plain declarations with no runtime imports,
 * so they run here exactly as the wizard runs them.
 */
const resolveInitialStep = (() => {
  const wanted = new Set([
    'resolveInitialStep',
    'resolveStepForPostgresMode',
    'normalizeServerStep'
  ]);
  const declarations = collect(
    hookFile,
    (node) => ts.isFunctionDeclaration(node) && node.name && wanted.has(node.name.text)
  ).map((node) => node.getText(hookFile));
  assert.equal(declarations.length, wanted.size, 'missing a wizard entry-step declaration');

  const harness = `${declarations.join('\n')}\nmodule.exports = { resolveInitialStep };`;
  const module = { exports: {} };
  new Function('module', 'exports', transpile(harness, ts.ModuleKind.CommonJS))(
    module,
    module.exports
  );
  return module.exports.resolveInitialStep;
})();

/**
 * Which component the wizard's own `renderStep` switch draws for each step id. Selecting a step the
 * switch has no arm for falls to `default: return null`, which paints the wizard chrome, the
 * progress bar and the step title around an empty body - a broken screen that every check on the
 * flow alone still calls correct.
 */
const componentByStep = (() => {
  const unwrap = (node) => {
    let current = node;
    while (current && ts.isParenthesizedExpression(current)) {
      current = current.expression;
    }
    return current;
  };

  const tagOf = (clause) => {
    const returned = collect(clause, (node) => ts.isReturnStatement(node))[0];
    const expression = unwrap(returned?.expression);
    if (!expression) return null;
    if (ts.isJsxSelfClosingElement(expression)) return expression.tagName.getText(modalFile);
    if (ts.isJsxElement(expression)) return expression.openingElement.tagName.getText(modalFile);
    return null;
  };

  const byStep = new Map();
  for (const clause of collect(modalFile, (node) => ts.isCaseClause(node))) {
    byStep.set(clause.expression.getText(modalFile).slice(1, -1), tagOf(clause));
  }
  return byStep;
})();

const install = (overrides) => ({
  isCompleted: true,
  hasProcessedLogs: true,
  needsPostgresCredentials: false,
  accountExists: true,
  currentSetupStep: null,
  dataSourceChoice: null,
  completedPlatforms: null,
  mode: 'embedded',
  postgresHost: null,
  postgresPort: null,
  postgresDatabase: null,
  postgresUser: null,
  ...overrides
});

/** What the three product expressions do for one installation, with no session. */
const screensFor = (setupStatus, authenticationEnabled) => {
  const adminAccountRequired = isAdminAccountRequired({
    setupCompleted: setupStatus.isCompleted === true,
    authenticationEnabled,
    accountExists: setupStatus.accountExists ?? null
  });
  const entryStep = resolveInitialStep(setupStatus, adminAccountRequired);

  return {
    adminAccountRequired,
    wizard: evaluate(wizardCondition, {
      setupCompleted: setupStatus.isCompleted,
      setupStatus,
      adminAccountRequired
    }),
    signIn: evaluate(signInCondition, {
      checkingAuth: false,
      authMode: 'unauthenticated',
      authenticationEnabled,
      adminAccountRequired
    }),
    step: entryStep,
    // Null both when the switch has no arm for the step and when its arm returns null.
    stepComponent: componentByStep.get(entryStep) ?? null
  };
};

test('a fresh installation still signs in first and still starts the wizard at database setup', () => {
  const fresh = install({
    isCompleted: false,
    hasProcessedLogs: false,
    needsPostgresCredentials: true,
    accountExists: false
  });

  const screens = screensFor(fresh, true);

  assert.equal(screens.adminAccountRequired, false);
  assert.equal(screens.signIn, true);
  assert.equal(screens.wizard, true);
  assert.equal(screens.step, 'database-setup');
  assert.equal(screens.stepComponent, 'DatabaseSetupStep');
});

test('an upgraded installation with no account opens the wizard at the account step', () => {
  const upgraded = install({ accountExists: false });

  const screens = screensFor(upgraded, true);

  assert.equal(screens.adminAccountRequired, true);
  assert.equal(screens.wizard, true);
  assert.equal(screens.signIn, false);
  assert.equal(screens.step, 'admin-account');
  // Selecting the step is not the same as drawing it: without this arm the operator gets the wizard
  // chrome around an empty body.
  assert.equal(screens.stepComponent, 'AdminAccountStep');
});

test('an installation running without authentication is left alone', () => {
  const authDisabled = install({ accountExists: false });

  const screens = screensFor(authDisabled, false);

  assert.equal(screens.adminAccountRequired, false);
  assert.equal(screens.wizard, false);
});

test('an installation that already has an account is left alone', () => {
  const screens = screensFor(install({}), true);

  assert.equal(screens.adminAccountRequired, false);
  assert.equal(screens.wizard, false);
  assert.equal(screens.signIn, true);
});

test('an unreadable account table leaves the installation on the sign-in screen', () => {
  const screens = screensFor(install({ accountExists: null }), true);

  assert.equal(screens.adminAccountRequired, false);
  assert.equal(screens.wizard, false);
  assert.equal(screens.signIn, true);
});

test('a completed installation still reopens the wizard for missing database credentials', () => {
  const screens = screensFor(install({ needsPostgresCredentials: true }), true);

  assert.equal(screens.wizard, true);
  assert.equal(screens.step, 'database-setup');
});

test('the wizard does not close itself on an installation that owes an account', () => {
  const completionExit = hookSource.slice(hookSource.indexOf('const checkSetupStatus'));

  assert.ok(
    completionExit.indexOf('if (adminAccountRequired)') <
      completionExit.indexOf('if (setupData.isCompleted)'),
    'the completion check runs before the account check and would close the wizard'
  );
});
