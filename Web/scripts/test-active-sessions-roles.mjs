import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import { bindLifted, liftConstArrow, transpile } from './transpile-module.mjs';

/**
 * The Users list sorts every session into one of two kinds: an account session, or a guest. An
 * ordinary account's session sits beside the primary administrator's under Signed in, the guest
 * filter lists guests only, and every row leads with the safe username/deleted/shared identity.
 * Sessions are tied to accounts by the account id the server sends, never by the user name.
 *
 * Each of those decisions is a separate expression in UserTab.tsx, so they are lifted out of the
 * product source and run here rather than restated. A restatement would keep passing after
 * someone narrowed one of them back to a literal comparison against 'admin'.
 */

const USER_TAB = 'src/components/features/user/UserTab.tsx';

const readWebSource = (relativePath) =>
  readFileSync(new URL(`../${relativePath}`, import.meta.url), 'utf8');

const parse = (fileName, relativePath, kind) =>
  ts.createSourceFile(fileName, readWebSource(relativePath), ts.ScriptTarget.Latest, true, kind);

const sessionTypesFile = parse(
  'types.ts',
  'src/components/features/user/types.ts',
  ts.ScriptKind.TS
);
const userTabFile = parse('UserTab.tsx', USER_TAB, ts.ScriptKind.TSX);

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

const isGuestSession = run(initializerOf(userTabFile, 'isGuestSession'), {});

const sessionOf = (sessionType) => ({ id: `session-${sessionType}`, sessionType });
const everySession = ['admin', 'user', 'guest'].map(sessionOf);
const guestSessions = run(initializerOf(userTabFile, 'guestSessions'), {
  sessions: everySession,
  isGuestSession
});

const countForFilter = (filter) =>
  run(initializerOf(userTabFile, 'counts'), {
    sessions: everySession,
    accounts: [],
    guestSessions,
    historySessions: []
  })[filter];

const listedUnderFilter = (filter) =>
  bindLifted(liftConstArrow(USER_TAB, 'filterRows'), {
    filter,
    sessions: everySession,
    guestSessions,
    historySessions: [],
    accounts: [],
    needle: '',
    matchingSessions: (list) => list,
    activeRow: (session) => session.sessionType,
    endedRow: (session) => session.sessionType,
    accountRow: (account) => account.id
  })().rows;

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

test('the safe account identity is nullable and the account id is optional', () => {
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
  // Guests and shared access have no account, and the server omits null fields.
  assert.equal(properties.get('accountId')?.type.getText(sessionTypesFile), 'string | null');
  assert.ok(properties.get('accountId')?.questionToken, 'accountId must tolerate being omitted');
});

test('a session belongs to an account by its account id, never by its user name', () => {
  const sessions = [
    { id: 'own', accountId: 'account-a', username: 'alice' },
    { id: 'same-name', accountId: 'account-b', username: 'alice' },
    { id: 'shared', username: 'alice' }
  ];
  const activeSessionsOf = bindLifted(liftConstArrow(USER_TAB, 'activeSessionsOf'), { sessions });
  assert.deepEqual(
    activeSessionsOf('account-a').map((session) => session.id),
    ['own']
  );

  const userFolder = new URL('../src/components/features/user/', import.meta.url);
  for (const file of readdirSync(userFolder).filter((name) => /\.tsx?$/.test(name))) {
    assert.doesNotMatch(
      readFileSync(new URL(file, userFolder), 'utf8'),
      /\busername\s*[!=]==|[!=]==\s*[\w.?]*\busername\b/,
      `${file} compares a user name`
    );
  }
});

test('primary administrator and ordinary user sessions are both account sessions', () => {
  assert.equal(isGuestSession(sessionOf('admin')), false);
  assert.equal(isGuestSession(sessionOf('user')), false);
  assert.equal(isGuestSession(sessionOf('guest')), true);
});

test('the filter counts both account kinds and leaves the guest count alone', () => {
  assert.equal(countForFilter('signedIn'), 3);
  assert.equal(countForFilter('guests'), 1);
});

test('the filtered list shows both account kinds and never lists either under guests', () => {
  assert.deepEqual(listedUnderFilter('signedIn'), ['admin', 'user', 'guest']);
  assert.deepEqual(listedUnderFilter('guests'), ['guest']);
});

test('account, deleted, guest and shared rows have distinct safe labels', () => {
  const sessionName = bindLifted(
    liftConstArrow('src/components/features/user/types.ts', 'sessionName'),
    {}
  );
  const t = (key) => key;
  assert.equal(sessionName({ ...sessionOf('user'), username: ' alice ' }, t), 'alice');
  assert.equal(
    sessionName({ ...sessionOf('user'), username: null, accountDeleted: true }, t),
    'activeSessions.deletedAccount'
  );
  assert.equal(
    sessionName({ ...sessionOf('guest'), username: null }, t),
    'activeSessions.filters.guest'
  );
  assert.equal(
    sessionName({ ...sessionOf('user'), username: null }, t),
    'activeSessions.sharedAccess'
  );

  assert.match(
    initializerOf(userTabFile, 'sessionMatchesSearch'),
    /session\.username\s*\?\?\s*''/,
    'username is missing from the search haystack'
  );
});
