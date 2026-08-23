import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import { compileToUrl, transpile } from './transpile-module.mjs';

const { isAdminAccountRequired } = await import(
  await compileToUrl('../src/utils/adminAccountSetup.ts')
);

/**
 * Two kinds of installation own no account row: a brand-new one, and one that has been running since
 * before accounts existed, which reports setup as completed and had every session it held revoked by
 * the upgrade. The screens either of them reaches are decided by a handful of expressions in two
 * files and by the order App.tsx writes its branches in, and getting any one of those wrong strands
 * the installation on a sign-in form with nothing to sign in with.
 *
 * So the expressions and that order are read out of the product source and run, rather than copied
 * here: a copy would keep passing after someone edited the original.
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

const wizardBranches = collect(
  appFile,
  (node) =>
    ts.isIfStatement(node) && node.expression.getText(appFile) === 'shouldShowInitializationFlow'
);
assert.equal(wizardBranches.length, 1, 'expected exactly one wizard branch in App.tsx');

/**
 * App.tsx returns from the first of the two branches that matches, so knowing that both conditions
 * are true says nothing about which screen the operator gets. Which one is written first is read
 * out of the source for the same reason the conditions are: writing the order down here would keep
 * passing after someone moved a branch.
 */
const signInComesFirst = signInConditions[0].pos < wizardBranches[0].pos;

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

/** What the product's own expressions do for one installation, with no session. */
const screensFor = (setupStatus, authenticationEnabled) => {
  const adminAccountRequired = isAdminAccountRequired({
    authenticationEnabled,
    accountExists: setupStatus.accountExists ?? null,
    needsPostgresCredentials: setupStatus.needsPostgresCredentials === true,
    mainAdminRecoveryAvailable: setupStatus.mainAdminRecoveryAvailable === true
  });
  const entryStep = resolveInitialStep(setupStatus, adminAccountRequired);

  const wizard = evaluate(wizardCondition, {
    setupCompleted: setupStatus.isCompleted,
    setupStatus,
    adminAccountRequired
  });
  const signIn = evaluate(signInCondition, {
    checkingAuth: false,
    checkingSetupStatus: false,
    authMode: 'unauthenticated',
    authenticationEnabled,
    adminAccountRequired
  });
  const inSourceOrder = signInComesFirst
    ? [
        ['sign-in', signIn],
        ['wizard', wizard]
      ]
    : [
        ['wizard', wizard],
        ['sign-in', signIn]
      ];

  return {
    adminAccountRequired,
    wizard,
    signIn,
    // The screen actually drawn, which is the earlier branch whenever both conditions hold.
    screen: inSourceOrder.find(([, matches]) => matches)?.[0] ?? 'app',
    step: entryStep,
    // Null both when the switch has no arm for the step and when its arm returns null.
    stepComponent: componentByStep.get(entryStep) ?? null
  };
};

test('a fresh installation creates its first account before it is asked to sign in', () => {
  const fresh = install({
    isCompleted: false,
    hasProcessedLogs: false,
    needsPostgresCredentials: true,
    accountExists: false
  });

  const screens = screensFor(fresh, true);

  // Nothing exists to sign in with here either: the account table is empty, so every username and
  // password is refused and the operator has no way to create the account the form is asking for.
  // The wizard has to come first, and it hands the operator back to the sign-in screen once the
  // account is made.
  assert.equal(screens.adminAccountRequired, true);
  assert.equal(screens.signIn, false);
  assert.equal(screens.wizard, true);
  assert.equal(screens.screen, 'wizard');
  assert.equal(screens.step, 'admin-account');
  assert.equal(screens.stepComponent, 'AdminAccountStep');
});

test('an upgraded installation with no account opens the wizard at the account step', () => {
  const upgraded = install({ accountExists: false });

  const screens = screensFor(upgraded, true);

  assert.equal(screens.adminAccountRequired, true);
  assert.equal(screens.wizard, true);
  assert.equal(screens.signIn, false);
  assert.equal(screens.screen, 'wizard');
  assert.equal(screens.step, 'admin-account');
  // Selecting the step is not the same as drawing it: without this arm the operator gets the wizard
  // chrome around an empty body.
  assert.equal(screens.stepComponent, 'AdminAccountStep');
});

test('an installation waiting for its database credentials opens the wizard at the database step', () => {
  // External Postgres started before the connection details were supplied. The server cannot read
  // the accounts table, so it reports the account state as unknown rather than as empty. Signing in
  // is impossible for the same reason the table cannot be read, so the sign-in screen is a dead end
  // and the wizard is the only screen that can take the credentials.
  const awaitingCredentials = install({
    isCompleted: false,
    hasProcessedLogs: false,
    needsPostgresCredentials: true,
    accountExists: null,
    mode: 'external'
  });

  const screens = screensFor(awaitingCredentials, true);

  assert.equal(screens.adminAccountRequired, true);
  assert.equal(screens.signIn, false);
  assert.equal(screens.screen, 'wizard');
  // The account row is stored in the database this step is about to configure, so the account step
  // has to wait for it.
  assert.equal(screens.step, 'external-db-form');
  assert.equal(screens.stepComponent, 'ExternalDatabaseSetupStep');
});

test('an installation whose database password was deleted still signs in first', () => {
  // The documented way to recover a forgotten embedded password is to delete the credentials file
  // and restart. The embedded server stays reachable, so the account state is known and sign-in
  // still works, and signing in is what carries a session into the database step. Sending this
  // installation to the wizard instead would land it on a step it cannot submit.
  const passwordForgotten = install({ needsPostgresCredentials: true });

  const screens = screensFor(passwordForgotten, true);

  assert.equal(screens.adminAccountRequired, false);
  assert.equal(screens.signIn, true);
  assert.equal(screens.screen, 'sign-in');
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

test('a completed installation with the recovery window open uses the account setup step', () => {
  const recovering = install({ mainAdminRecoveryAvailable: true });

  const screens = screensFor(recovering, true);

  assert.equal(screens.adminAccountRequired, true);
  assert.equal(screens.signIn, false);
  assert.equal(screens.wizard, true);
  assert.equal(screens.screen, 'wizard');
  assert.equal(screens.step, 'admin-account');
  assert.equal(screens.stepComponent, 'AdminAccountStep');
});

test('an unreadable account table leaves the installation on the sign-in screen', () => {
  // The database went away but its connection details are still on disk, so the answer can arrive
  // on its own. This installation belongs on sign-in and stays there.
  const screens = screensFor(install({ accountExists: null }), true);

  assert.equal(screens.adminAccountRequired, false);
  assert.equal(screens.wizard, false);
  assert.equal(screens.signIn, true);
  assert.equal(screens.screen, 'sign-in');
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
