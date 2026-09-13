import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const readWebSource = (relativePath) =>
  readFileSync(new URL(`../${relativePath}`, import.meta.url), 'utf8');

const activeSource = readWebSource('src/components/features/user/ActiveSessions.tsx');
const typeSource = readWebSource('src/components/features/user/types.ts');
const userCss = readWebSource('src/styles/features/user.css');

test('the browser session contract exposes safe identity without an account key', () => {
  assert.match(
    activeSource,
    /import\s*\{[\s\S]*type Session[\s\S]*\}\s*from '\.\/types'/,
    'ActiveSessions must consume the shared typed Session contract'
  );
  assert.match(typeSource, /\busername\?:\s*string\s*\|\s*null\s*;/);
  assert.match(typeSource, /\baccountDeleted:\s*boolean\s*;/);
  assert.doesNotMatch(typeSource, /\baccountId\??\s*:/, 'database account ids must remain private');
});

test('session rows lead with who, then where, then when', () => {
  for (const key of [
    'activeSessions.account',
    'activeSessions.deletedAccount',
    'activeSessions.sharedAccess',
    'activeSessions.filters.guest',
    'activeSessions.labels.lastSeen'
  ]) {
    assert.ok(activeSource.includes(key), `missing identity label ${key}`);
  }

  const identity = activeSource.indexOf('session-row__identity');
  const where = activeSource.indexOf('session-row__where', identity);
  const when = activeSource.indexOf('session-row__when', where);
  assert.ok(
    identity >= 0 && where > identity && when > where,
    'row scan order is not who/where/when'
  );
  assert.match(activeSource, /session\.username\s*\?\?\s*''/);
  assert.doesNotMatch(activeSource, /session\.accountId/);
});

test('session loads stage every page and reject late identity responses', () => {
  const firstLoad = activeSource.indexOf('const first = await ApiService.getSessions');
  const restLoad = activeSource.indexOf('await Promise.all', firstLoad);
  const commit = activeSource.indexOf('setSessions(loadedSessions)', restLoad);

  assert.ok(
    firstLoad >= 0 && restLoad > firstLoad && commit > restLoad,
    'pages commit before staging'
  );
  assert.match(activeSource, /request !== loadRequestRef\.current/);
  assert.match(activeSource, /requestedIdentity !== sessionIdentityRef\.current/);
  assert.match(activeSource, /useLayoutEffect\(\(\) => \{[\s\S]*loadRequestRef\.current \+= 1/);
  assert.match(activeSource, /setSearchQuery\(''\)[\s\S]*onFilterChange\('all'\)/);
  assert.match(activeSource, /`shared:\$\{sessionId \?\? 'none'\}`/);
  assert.match(activeSource, /sessionsRef\.current = \[\][\s\S]*setSessions\(\[\]\)/);
});

test('initial and refresh failures remain visibly distinct', () => {
  assert.match(activeSource, /<ErrorBlock[\s\S]*activeSessions\.initialLoadFailed/);
  assert.match(activeSource, /activeSessions\.initialLoadFailedMessage/);
  assert.match(activeSource, /activeSessions\.retry/);
  assert.match(activeSource, /const hasSnapshot =[\s\S]*setRefreshFailed\(true\)/);
  assert.match(
    activeSource,
    /<Alert color="error">\{t\('activeSessions\.refreshFailed'\)\}<\/Alert>/
  );
  assert.match(activeSource, /!initialLoadFailed && activeSessions\.length === 0/);
  assert.match(activeSource, /activeSessions\.noMatches/);
});

test('destructive row actions use visible menus and nested controls keep disclosure separate', () => {
  assert.equal((activeSource.match(/<RowActionsMenu/g) ?? []).length, 2);
  assert.equal(activeSource.includes('revealOnHover'), false);
  assert.match(activeSource, /rowToggleHandlers\(\(\) => toggleSessionExpanded\(session\.id\)\)/);
  assert.match(activeSource, /activeSessions\.sessionActions/);
  assert.match(activeSource, /activeSessions\.showDetails/);
  assert.match(activeSource, /activeSessions\.hideDetails/);
  assert.match(activeSource, /rememberSessionActionFocus/);
  assert.match(activeSource, /sessionRowsRef\.current\.get\(nextSessionId\)/);
  assert.match(activeSource, /sessionConsoleRef\.current\?\.querySelector<HTMLElement>/);
});

test('technical session identity stays inside disclosure content', () => {
  const firstDisclosure = activeSource.indexOf('<CollapsibleRegion');
  const firstSessionId = activeSource.indexOf('activeSessions.labels.sessionIdWithValue');
  assert.ok(firstDisclosure >= 0 && firstSessionId > firstDisclosure);
  assert.match(activeSource, /session-detail__client-ip/);
  assert.match(activeSource, /activeSessions\.labels\.publicIp/);
  assert.match(activeSource, /activeSessions\.labels\.location/);
  assert.match(activeSource, /activeSessions\.labels\.lastSeenShort/);
});

test('session layout keeps long content and phone controls within the row', () => {
  assert.match(userCss, /\.session-row > \.mgmt-row__body[\s\S]*min-width:\s*0/);
  assert.match(userCss, /\.session-row__identity[\s\S]*overflow-wrap:\s*anywhere/);
  assert.match(userCss, /\.session-row__where > span,[\s\S]*overflow-wrap:\s*anywhere/);
  assert.match(userCss, /\.session-row__actions[\s\S]*opacity:\s*1/);
  assert.doesNotMatch(userCss, /\.session-row:hover \.session-row__actions/);
  assert.match(userCss, /\.session-row__actions button,[\s\S]*min-height:\s*2\.75rem/);
  assert.match(userCss, /\.session-detail \.mgmt-stat__value[\s\S]*overflow-wrap:\s*anywhere/);
});

test('published session copy exists in both locales', () => {
  const keys = [
    'account',
    'deletedAccount',
    'sharedAccess',
    'initialLoadFailed',
    'initialLoadFailedMessage',
    'refreshFailed',
    'retry',
    'noMatches',
    'showDetails',
    'hideDetails',
    'sessionActions',
    'accountFilter'
  ];

  for (const locale of ['en', 'zh']) {
    const messages = JSON.parse(readWebSource(`src/i18n/locales/${locale}.json`));
    for (const key of keys) {
      assert.equal(typeof messages.activeSessions?.[key], 'string', `${locale} is missing ${key}`);
    }
  }
});
