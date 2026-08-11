import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import { transpile } from './transpile-module.mjs';

/**
 * An admin session and a user session are both account sessions and reach the same screens; a guest
 * reaches less. Which screens each one gets is decided by six expressions spread over five files,
 * and two of them are the same decision written twice - the management nav disables a section once
 * for its desktop row and once for its mobile row, so editing one and not the other gives the same
 * account two different interfaces depending on how wide the window is.
 *
 * The expressions are therefore lifted out of the product source and run here rather than restated:
 * a restatement would keep passing after someone changed the original.
 */

const readWebSource = (relativePath) =>
  readFileSync(new URL(`../${relativePath}`, import.meta.url), 'utf8');

const parse = (fileName, relativePath, kind) =>
  ts.createSourceFile(fileName, readWebSource(relativePath), ts.ScriptTarget.Latest, true, kind);

const authContextFile = parse('AuthContext.tsx', 'src/contexts/AuthContext.tsx', ts.ScriptKind.TSX);
const authServiceFile = parse('auth.service.ts', 'src/services/auth.service.ts', ts.ScriptKind.TS);
const navigationFile = parse(
  'Navigation.tsx',
  'src/components/layout/Navigation.tsx',
  ts.ScriptKind.TSX
);
const managementNavFile = parse(
  'ManagementNav.tsx',
  'src/components/features/management/ManagementNav.tsx',
  ts.ScriptKind.TSX
);
const managementTabFile = parse(
  'ManagementTab.tsx',
  'src/components/features/management/ManagementTab.tsx',
  ts.ScriptKind.TSX
);
const appFile = parse('App.tsx', 'src/App.tsx', ts.ScriptKind.TSX);
const preferencesFile = parse(
  'SessionPreferencesContext.tsx',
  'src/contexts/SessionPreferencesContext.tsx',
  ts.ScriptKind.TSX
);

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

const only = (nodes, description) => {
  assert.equal(nodes.length, 1, description);
  return nodes[0];
};

/** The initializer of a named `const`, as source text. */
const initializerOf = (sourceFile, name) => {
  const declaration = only(
    collect(
      sourceFile,
      (node) => ts.isVariableDeclaration(node) && node.name.getText(sourceFile) === name
    ),
    `expected exactly one ${name} declaration in ${sourceFile.fileName}`
  );
  assert.ok(declaration.initializer, `${name} has no initializer`);
  return declaration.initializer.getText(sourceFile);
};

/** Runs one expression lifted out of product source, with the values it reads supplied by name. */
const run = (expression, bindings) => {
  const names = Object.keys(bindings);
  const compiled = transpile(`const lifted = (${expression});`);
  return new Function(...names, `${compiled}\nreturn lifted;`)(
    ...names.map((name) => bindings[name])
  );
};

/** The literal-valued properties of an object literal; `icon` and `label` are expressions and drop out. */
const literalRecord = (sourceFile, objectLiteral) => {
  const record = {};
  for (const property of objectLiteral.properties) {
    if (!ts.isPropertyAssignment(property)) continue;
    const value = property.initializer;
    const name = property.name.getText(sourceFile);
    if (ts.isStringLiteral(value)) record[name] = value.text;
    else if (ts.isNumericLiteral(value)) record[name] = Number(value.text);
    else if (value.kind === ts.SyntaxKind.TrueKeyword) record[name] = true;
    else if (value.kind === ts.SyntaxKind.FalseKeyword) record[name] = false;
  }
  return record;
};

const arrayLiteralOf = (sourceFile, name) => {
  const declaration = only(
    collect(
      sourceFile,
      (node) =>
        ts.isVariableDeclaration(node) &&
        node.name.getText(sourceFile) === name &&
        node.initializer !== undefined &&
        ts.isArrayLiteralExpression(node.initializer)
    ),
    `expected exactly one ${name} array in ${sourceFile.fileName}`
  );
  return declaration.initializer.elements.map((element) => literalRecord(sourceFile, element));
};

/**
 * `isAccountHolder` is a plain declaration with no runtime imports, so it runs here exactly as the
 * browser runs it.
 */
const isAccountHolder = (() => {
  const declaration = only(
    collect(
      authServiceFile,
      (node) => ts.isFunctionDeclaration(node) && node.name?.text === 'isAccountHolder'
    ),
    'auth.service.ts should declare isAccountHolder exactly once'
  );
  const harness = `${declaration.getText(authServiceFile)}\nmodule.exports = { isAccountHolder };`;
  const compiled = { exports: {} };
  new Function('module', 'exports', transpile(harness, ts.ModuleKind.CommonJS))(
    compiled,
    compiled.exports
  );
  return compiled.exports.isAccountHolder;
})();

/** The if/else chain in `fetchAuth` that turns a session type into an auth mode. */
const authModeChain = only(
  collect(
    authContextFile,
    (node) =>
      ts.isIfStatement(node) &&
      node.getText(authContextFile).includes("setAuthMode('authenticated')")
  ),
  'AuthContext should choose the auth mode in exactly one if/else chain'
).getText(authContextFile);

const authModeFor = (sessionType) => {
  let authMode = null;
  new Function('data', 'isAccountHolder', 'setAuthMode', authModeChain)(
    { isAuthenticated: true, sessionType },
    isAccountHolder,
    (mode) => {
      authMode = mode;
    }
  );
  return authMode;
};

const isAdminExpression = initializerOf(authContextFile, 'isAdmin');
const isAdminFor = (sessionType) => run(isAdminExpression, { authMode: authModeFor(sessionType) });

const navigationTabs = arrayLiteralOf(navigationFile, 'allTabs');
const navigationFilter = only(
  collect(
    navigationFile,
    (node) =>
      ts.isCallExpression(node) && node.expression.getText(navigationFile) === 'allTabs.filter'
  ),
  'Navigation should filter its tabs exactly once'
).arguments[0].getText(navigationFile);

/**
 * The session type is supplied alongside the values these expressions read today, so that a decision
 * that starts reading the role directly is still evaluated per role here rather than blowing up.
 */
const visibleTabIds = (sessionType) => {
  const keep = run(navigationFilter, {
    authMode: authModeFor(sessionType),
    sessionType,
    prefillEnabled: false,
    isBanned: false,
    dockerAvailable: true
  });
  return navigationTabs.filter((tab) => keep(tab)).map((tab) => tab.id);
};

const managementSectionIds = arrayLiteralOf(managementNavFile, 'tabs').map((tab) => tab.id);
const isDisabledExpressions = collect(
  managementNavFile,
  (node) => ts.isVariableDeclaration(node) && node.name.getText(managementNavFile) === 'isDisabled'
).map((node) => node.initializer.getText(managementNavFile));

/** Per section, whether the desktop row and the mobile row each leave it usable. */
const sectionsEnabledFor = (sessionType) =>
  managementSectionIds.map((id) => ({
    id,
    rows: isDisabledExpressions.map(
      (expression) => !run(expression, { tab: { id }, isAdmin: isAdminFor(sessionType) })
    )
  }));

const tabSwitchConditions = collect(
  appFile,
  (node) =>
    ts.isIfStatement(node) &&
    node.thenStatement.getText(appFile).includes("handleTabChange('dashboard')")
).map((node) => node.expression.getText(appFile));

const forcedOffTab = (sessionType, activeTab, prefillEnabled = false) =>
  tabSwitchConditions.some((condition) =>
    run(condition, { authMode: authModeFor(sessionType), sessionType, activeTab, prefillEnabled })
  );

const preferencesAuthFields = only(
  collect(
    preferencesFile,
    (node) =>
      ts.isVariableDeclaration(node) && node.initializer?.getText(preferencesFile) === 'useAuth()'
  ),
  'SessionPreferencesContext should read the auth context exactly once'
).name.getText(preferencesFile);

const resetSkipConditions = collect(
  preferencesFile,
  (node) =>
    ts.isIfStatement(node) && node.expression.getText(preferencesFile).includes('sessionType ===')
).map((node) => node.expression.getText(preferencesFile));

/** Whether a broadcast preference reset leaves this session's cached preferences alone. */
const ignoresReset = (sessionType, resetScope) =>
  resetSkipConditions.some((condition) =>
    run(condition, { sessionType: resetScope, isAdmin: isAdminFor(sessionType) })
  );

const otherSessionRefusal = only(
  collect(
    preferencesFile,
    (node) =>
      ts.isIfStatement(node) &&
      node.expression.getText(preferencesFile).includes('!isAdmin') &&
      node.expression.getText(preferencesFile).includes('currentSession')
  ),
  'SessionPreferencesContext should gate other sessions in exactly one place'
).expression.getText(preferencesFile);

const loadsOtherSessions = (sessionType) =>
  !run(otherSessionRefusal, {
    sessionId: 'another-session',
    currentSession: 'my-session',
    isAdmin: isAdminFor(sessionType)
  });

const guestDefaultsGate = only(
  collect(
    preferencesFile,
    (node) =>
      ts.isIfStatement(node) &&
      node.expression.getText(preferencesFile) === 'isAdmin || !hasSession'
  ),
  'SessionPreferencesContext should gate the guest defaults in exactly one place'
).expression.getText(preferencesFile);

const inheritsGuestDefaults = (sessionType) =>
  !run(guestDefaultsGate, { isAdmin: isAdminFor(sessionType), hasSession: true });

test('a user signs in as an account session, exactly as an admin does', () => {
  assert.equal(authModeFor('admin'), 'authenticated');
  assert.equal(authModeFor('user'), authModeFor('admin'));
  assert.equal(authModeFor('guest'), 'guest');

  assert.equal(isAdminFor('user'), true);
  assert.equal(isAdminFor('user'), isAdminFor('admin'));
  assert.equal(isAdminFor('guest'), false);
});

test('a user sees every top navigation tab an admin sees', () => {
  const admin = visibleTabIds('admin');

  assert.deepEqual(visibleTabIds('user'), admin);
  for (const id of [
    'dashboard',
    'downloads',
    'clients',
    'prefill',
    'users',
    'events',
    'management'
  ]) {
    assert.ok(admin.includes(id), `an admin should see the ${id} tab`);
  }
});

test('a guest still sees fewer top navigation tabs', () => {
  const guest = visibleTabIds('guest');

  assert.ok(!guest.includes('management'), 'a guest should not see the management tab');
  assert.ok(!guest.includes('users'), 'a guest should not see the users tab');
  assert.ok(guest.includes('authenticate'), 'a guest should see the sign-in tab');
  assert.notDeepEqual(guest, visibleTabIds('admin'));
});

test('every management section is as usable for a user as for an admin, in both rows', () => {
  assert.equal(managementSectionIds.length, 9);
  assert.equal(
    isDisabledExpressions.length,
    2,
    'the desktop row and the mobile row each decide this'
  );

  const admin = sectionsEnabledFor('admin');
  assert.deepEqual(sectionsEnabledFor('user'), admin);

  for (const section of admin) {
    assert.deepEqual(section.rows, [true, true], `${section.id} should be usable in both rows`);
  }
});

test('a guest still reaches only the settings section', () => {
  for (const section of sectionsEnabledFor('guest')) {
    const expected = section.id === 'settings';
    assert.deepEqual(
      section.rows,
      [expected, expected],
      `${section.id} should ${expected ? '' : 'not '}be usable for a guest, in both rows`
    );
  }
});

test('the management nav is handed the account-session flag from the auth context', () => {
  assert.match(
    only(
      collect(
        managementTabFile,
        (node) =>
          ts.isVariableDeclaration(node) &&
          node.initializer?.getText(managementTabFile) === 'useAuth()'
      ),
      'ManagementTab should read the auth context exactly once'
    ).name.getText(managementTabFile),
    /\bisAdmin\b/
  );

  const element = only(
    collect(
      managementTabFile,
      (node) =>
        ts.isJsxSelfClosingElement(node) &&
        node.tagName.getText(managementTabFile) === 'ManagementNav'
    ),
    'ManagementTab should render ManagementNav exactly once'
  );
  const attribute = element.attributes.properties.find(
    (property) =>
      ts.isJsxAttribute(property) && property.name.getText(managementTabFile) === 'isAdmin'
  );
  assert.ok(attribute, 'ManagementNav should be given the isAdmin flag');
  assert.equal(attribute.initializer.getText(managementTabFile), '{isAdmin}');
});

test('neither tab switch pushes a user off a tab an admin keeps', () => {
  assert.equal(tabSwitchConditions.length, 2, 'App should switch tabs away in exactly two places');

  for (const id of visibleTabIds('admin')) {
    assert.equal(forcedOffTab('user', id), forcedOffTab('admin', id), `the ${id} tab`);
    assert.equal(forcedOffTab('user', id), false, `a user should keep the ${id} tab`);
  }
});

test('a guest that loses prefill access is still moved off the prefill tab', () => {
  assert.equal(forcedOffTab('guest', 'prefill'), true);
  assert.equal(forcedOffTab('guest', 'prefill', true), false);
  assert.equal(forcedOffTab('guest', 'management'), true);
});

test('the preference cache treats a user as an account session and still scopes a guest', () => {
  assert.match(preferencesAuthFields, /\bisAdmin\b/);
  assert.doesNotMatch(
    preferencesAuthFields,
    /\bsessionType\b/,
    'the preference cache should scope on the account-session flag, not on the role'
  );
  assert.equal(resetSkipConditions.length, 2);

  assert.equal(ignoresReset('user', 'guest'), ignoresReset('admin', 'guest'));
  assert.equal(ignoresReset('user', 'guest'), true, 'a guest-scoped reset leaves an account alone');
  assert.equal(ignoresReset('guest', 'guest'), false, 'a guest-scoped reset clears a guest');
  for (const sessionType of ['admin', 'user', 'guest']) {
    assert.equal(
      ignoresReset(sessionType, undefined),
      false,
      'a reset with no scope clears every session'
    );
  }

  assert.equal(loadsOtherSessions('user'), loadsOtherSessions('admin'));
  assert.equal(loadsOtherSessions('user'), true);
  assert.equal(loadsOtherSessions('guest'), false);

  assert.equal(inheritsGuestDefaults('user'), inheritsGuestDefaults('admin'));
  assert.equal(inheritsGuestDefaults('user'), false);
  assert.equal(inheritsGuestDefaults('guest'), true);
});
