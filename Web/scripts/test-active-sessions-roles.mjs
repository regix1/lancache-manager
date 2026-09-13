import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import { transpile } from './transpile-module.mjs';

/**
 * The sessions list sorts every row into one of two buckets: an account session, or a guest. An
 * ordinary account belongs in the account bucket next to the primary administrator while the
 * presentation leads with the safe username/deleted/shared identity.
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

test('the safe account identity is nullable and never exposes an account id', () => {
  const session = only(
    collect(
      sessionTypesFile,
      (node) => ts.isInterfaceDeclaration(node) && node.name.text === 'Session'
    ),
    'the shared user types should declare Session exactly once'
  );
  const properties = new Map(
    session.members
      .filter(ts.isPropertySignature)
      .map((node) => [node.name.getText(sessionTypesFile), node])
  );

  assert.equal(properties.get('username')?.type.getText(sessionTypesFile), 'string | null');
  assert.ok(
    properties.get('username')?.questionToken,
    'username must tolerate omitted null values'
  );
  assert.equal(properties.get('accountDeleted')?.type.getText(sessionTypesFile), 'boolean');
  assert.equal(properties.has('accountId'), false, 'the browser model must not expose account ids');
});

test('primary administrator and ordinary user sessions both count as account sessions', () => {
  assert.equal(isAdminSession(sessionOf('admin')), true);
  assert.equal(isAdminSession(sessionOf('user')), isAdminSession(sessionOf('admin')));
  assert.equal(isAdminSession(sessionOf('guest')), false);

  assert.equal(isGuestSession(sessionOf('user')), false);
  assert.equal(isGuestSession(sessionOf('guest')), true);
});

test('the filter counts both account kinds and leaves the guest count alone', () => {
  assert.equal(countForFilter('all'), 3);
  assert.equal(countForFilter('admin'), 2, 'an admin and a user are both account sessions');
  assert.equal(countForFilter('guest'), 1);
});

test('the filtered list shows both account kinds and never lists either under guests', () => {
  assert.deepEqual(listedUnderFilter('all'), ['admin', 'user', 'guest']);
  assert.deepEqual(listedUnderFilter('admin'), ['admin', 'user']);
  assert.deepEqual(listedUnderFilter('guest'), ['guest']);
});

test('account, deleted, guest and shared rows have distinct safe labels', () => {
  const source = activeSessionsFile.getText();
  for (const key of [
    'activeSessions.account',
    'activeSessions.deletedAccount',
    'activeSessions.sharedAccess',
    'activeSessions.filters.guest',
    'activeSessions.labels.guestBadge'
  ]) {
    assert.ok(source.includes(key), `ActiveSessions.tsx does not render ${key}`);
  }

  const search = initializerOf(activeSessionsFile, 'sessionMatchesSearch');
  assert.match(
    search,
    /session\.username\s*\?\?\s*''/,
    'username is missing from the search haystack'
  );
  assert.doesNotMatch(source, /session\.accountId/, 'the UI must not consume an account id');
});
