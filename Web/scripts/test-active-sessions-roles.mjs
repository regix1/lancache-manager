import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import { transpile } from './transpile-module.mjs';

/**
 * The sessions list sorts every row into one of two buckets: an account session, or a guest. A user
 * signs in against an account, so it belongs in the account bucket next to an admin - in the filter
 * counts, in the filtered list, on the row badge and in the confirmation copy.
 *
 * Each of those decisions is a separate expression in ActiveSessions.tsx, so they are lifted out of
 * the product source and run here rather than restated. A restatement would keep passing after
 * someone narrowed one of them back to a literal comparison against 'admin'.
 */

const readWebSource = (relativePath) =>
  readFileSync(new URL(`../${relativePath}`, import.meta.url), 'utf8');

const parse = (fileName, relativePath, kind) =>
  ts.createSourceFile(fileName, readWebSource(relativePath), ts.ScriptTarget.Latest, true, kind);

const authServiceFile = parse('auth.service.ts', 'src/services/auth.service.ts', ts.ScriptKind.TS);
const sessionTypesFile = parse(
  'types.ts',
  'src/components/features/user/types.ts',
  ts.ScriptKind.TS
);
const activeSessionsFile = parse(
  'ActiveSessions.tsx',
  'src/components/features/user/ActiveSessions.tsx',
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

const isAdminSession = run(initializerOf(activeSessionsFile, 'isAdminSession'), {
  isAccountHolder
});
const isGuestSession = run(initializerOf(activeSessionsFile, 'isGuestSession'), {
  isAccountHolder
});

const sessionOf = (sessionType) => ({ id: `session-${sessionType}`, sessionType });
const everySession = ['admin', 'user', 'guest'].map(sessionOf);

const countForFilter = (filter) =>
  run(initializerOf(activeSessionsFile, 'getCountForFilter'), {
    activeSessions: everySession,
    isAdminSession,
    isGuestSession
  })(filter);

const listedUnderFilter = (filter) =>
  run(initializerOf(activeSessionsFile, 'typeFilteredSessions'), {
    activeFilterValue: filter,
    activeSessions: everySession,
    isAdminSession,
    isGuestSession
  }).map((session) => session.sessionType);

/** The row badge, both rows: the desktop one and the mobile one each pick their own label. */
const badgeConditionals = collect(
  activeSessionsFile,
  (node) =>
    ts.isConditionalExpression(node) &&
    node.condition.getText(activeSessionsFile) === 'admin' &&
    ts.isCallExpression(node.whenTrue) &&
    ts.isCallExpression(node.whenFalse)
).map((node) => node.getText(activeSessionsFile));

const badgeLabels = (sessionType) =>
  badgeConditionals.map((conditional) =>
    run(conditional, { admin: isAdminSession(sessionOf(sessionType)), t: (key) => key })
  );

/** The revoke, delete and edit copy, which each name the kind of session being acted on. */
const sessionKindConditionals = collect(
  activeSessionsFile,
  (node) =>
    ts.isConditionalExpression(node) &&
    node.condition.getText(activeSessionsFile).includes('isAdminSession(')
).map((node) => node.getText(activeSessionsFile));

const sessionKindLabels = (sessionType) => {
  const session = sessionOf(sessionType);
  return sessionKindConditionals.map((conditional) =>
    run(conditional, {
      pendingRevokeSession: session,
      pendingDeleteSession: session,
      editingSession: session,
      isAdminSession,
      t: (key) => key
    })
  );
};

test('the sessions list reads the shared session type, so a user is a value it can hold', () => {
  const property = only(
    collect(
      sessionTypesFile,
      (node) =>
        ts.isPropertySignature(node) && node.name.getText(sessionTypesFile) === 'sessionType'
    ),
    'the Session row should declare sessionType exactly once'
  );

  assert.equal(
    property.type.getText(sessionTypesFile),
    'SessionType',
    'sessionType should be the shared session type, not a union the server has outgrown'
  );
  assert.match(
    only(
      collect(
        sessionTypesFile,
        (node) =>
          ts.isImportDeclaration(node) &&
          node.moduleSpecifier.getText(sessionTypesFile).includes('auth.service')
      ),
      'types.ts should import from the auth service exactly once'
    ).getText(sessionTypesFile),
    /\bSessionType\b/
  );
});

test('a user session counts as an account session, exactly as an admin does', () => {
  assert.equal(isAdminSession(sessionOf('admin')), true);
  assert.equal(isAdminSession(sessionOf('user')), isAdminSession(sessionOf('admin')));
  assert.equal(isAdminSession(sessionOf('guest')), false);

  assert.equal(isGuestSession(sessionOf('user')), false);
  assert.equal(isGuestSession(sessionOf('guest')), true);
});

test('the filter counts a user with the admins and leaves the guest count alone', () => {
  assert.equal(countForFilter('all'), 3);
  assert.equal(countForFilter('admin'), 2, 'an admin and a user are both account sessions');
  assert.equal(countForFilter('guest'), 1);
});

test('the filtered list shows a user alongside the admins and never under guests', () => {
  assert.deepEqual(listedUnderFilter('all'), ['admin', 'user', 'guest']);
  assert.deepEqual(listedUnderFilter('admin'), ['admin', 'user']);
  assert.deepEqual(listedUnderFilter('guest'), ['guest']);
});

test('a user row carries the account badge in both rows, not the guest one', () => {
  assert.equal(badgeConditionals.length, 2, 'the desktop row and the mobile row each label this');
  assert.equal(
    collect(
      activeSessionsFile,
      (node) => ts.isVariableDeclaration(node) && node.name.getText(activeSessionsFile) === 'admin'
    ).length,
    2,
    'both rows should derive the badge from the same account-session check'
  );
  for (const declaration of collect(
    activeSessionsFile,
    (node) => ts.isVariableDeclaration(node) && node.name.getText(activeSessionsFile) === 'admin'
  )) {
    assert.equal(declaration.initializer.getText(activeSessionsFile), 'isAdminSession(session)');
  }

  assert.deepEqual(badgeLabels('user'), badgeLabels('admin'));
  assert.notDeepEqual(badgeLabels('user'), badgeLabels('guest'));
});

test('the revoke, delete and edit copy calls a user an account session, not a guest', () => {
  assert.equal(sessionKindConditionals.length, 3, 'revoke, delete and edit each name the kind');

  assert.deepEqual(sessionKindLabels('user'), sessionKindLabels('admin'));
  assert.notDeepEqual(sessionKindLabels('user'), sessionKindLabels('guest'));
});
